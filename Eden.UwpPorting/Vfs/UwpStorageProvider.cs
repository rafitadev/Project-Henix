using Eden.UwpPorting.Core;
using System.Diagnostics;
using K4os.Compression.LZ4;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Linq;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;

namespace Eden.UwpPorting.Vfs;

public interface IVirtualFileSystem
{
    Task InitializeAsync(CancellationToken ct = default);
    Task TryLoadSwitchKeysAsync(CancellationToken ct = default);
    Task<StorageFile?> PickGameFileAsync();
    Task PickAndImportKeysAsync();
}

public static class EdenKeyManager
{
    private static readonly Dictionary<string, byte[]> Keys = new(StringComparer.OrdinalIgnoreCase);

    public static void LoadFromLines(IEnumerable<string> lines)
    {
        Keys.Clear();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int sep = line.IndexOf('=');
            if (sep <= 0) continue;

            string name = line[..sep].Trim();
            string hex = line[(sep + 1)..].Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
            if (hex.Length % 2 != 0) continue;

            byte[] key = Convert.FromHexString(hex);
            Keys[name] = key;
        }
    }

    public static IReadOnlyDictionary<string, byte[]> Snapshot() => Keys;
}

public sealed class UwpStorageProvider : IVirtualFileSystem
{
    private StorageFolder? _local;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _local = ApplicationData.Current.LocalFolder;
        await _local.CreateFolderAsync("keys", CreationCollisionOption.OpenIfExists);
        await _local.CreateFolderAsync("roms", CreationCollisionOption.OpenIfExists);
    }

    public async Task TryLoadSwitchKeysAsync(CancellationToken ct = default)
    {
        StorageFolder keysFolder = await ApplicationData.Current.LocalFolder.GetFolderAsync("keys");
        StorageFile? prod = await TryGetFileAsync(keysFolder, "prod.keys");
        if (prod is null)
        {
            BootDiagnostics.Warn("prod.keys não encontrado; continuando para permitir boot de homebrew sem criptografia.");
            return;
        }

        string content = await FileIO.ReadTextAsync(prod);
        EdenKeyManager.LoadFromLines(content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
    }

    public async Task PickAndImportKeysAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".keys");
        picker.SuggestedStartLocation = PickerLocationId.Downloads;

        StorageFile? source = await picker.PickSingleFileAsync();
        if (source is null) return;

        var keysFolder = await ApplicationData.Current.LocalFolder.GetFolderAsync("keys");
        string normalized = source.Name.Equals("title.keys", StringComparison.OrdinalIgnoreCase) ? "title.keys" : "prod.keys";
        await source.CopyAsync(keysFolder, normalized, NameCollisionOption.ReplaceExisting);
    }

    public async Task<StorageFile?> PickGameFileAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".xci");
        picker.FileTypeFilter.Add(".nsp");
        picker.FileTypeFilter.Add(".nro");
        picker.SuggestedStartLocation = PickerLocationId.Downloads;

        var file = await picker.PickSingleFileAsync();
        if (file is null) return null;

        StorageApplicationPermissions.FutureAccessList.AddOrReplace("last_game", file);
        RuntimeState.LoadedImage = await LoadRuntimeImageAsync(file);

        return file;
    }

    private static async Task<RuntimeImage> LoadRuntimeImageAsync(StorageFile file)
    {
        using var stream = await file.OpenStreamForReadAsync();
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        byte[] header = reader.ReadBytes(0x120);
        stream.Position = 0;

        string at0 = Encoding.ASCII.GetString(header, 0, Math.Min(4, header.Length));
        string at10 = header.Length >= 0x14 ? Encoding.ASCII.GetString(header, 0x10, 4) : string.Empty;
        string at100 = header.Length >= 0x104 ? Encoding.ASCII.GetString(header, 0x100, 4) : string.Empty;

        BootDiagnostics.Info($"Detect magic @0={at0} @0x10={at10} @0x100={at100}");

        if (at10 == "NRO0") return ReadNro(stream);
        if (at0 == "PFS0") return ReadFromPfs0(stream);
        if (at100 == "HFS0" || at0 == "HEAD") return ReadFromXci(stream);

        throw new InvalidDataException($"Formato não suportado: @0={at0} @0x10={at10} @0x100={at100}");
    }

    private static RuntimeImage ReadFromXci(Stream stream)
    {
        stream.Position = 0x100;
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        string rootMagic = Encoding.ASCII.GetString(br.ReadBytes(4));
        if (!rootMagic.Equals("HFS0", StringComparison.Ordinal)) throw new InvalidDataException("XCI inválido: HFS0 ausente.");

        int fileCount = br.ReadInt32();
        int stringTableSize = br.ReadInt32();
        _ = br.ReadInt32();

        var entries = new List<(long Offset, long Size, int NameOffset)>();
        for (int i = 0; i < fileCount; i++)
        {
            long offset = br.ReadInt64();
            long size = br.ReadInt64();
            int nameOffset = br.ReadInt32();
            _ = br.ReadInt32();
            entries.Add((offset, size, nameOffset));
        }

        long stringTablePos = stream.Position;
        byte[] stringTable = br.ReadBytes(stringTableSize);
        var ncaCandidates = new List<(string Name, byte[] Data)>();
        foreach (var e in entries)
        {
            string name = ReadCString(stringTable, e.NameOffset);

            stream.Position = dataOffset + e.Offset;
            byte[] blob = br.ReadBytes((int)e.Size);

            if (name.EndsWith(".nro", true, CultureInfo.InvariantCulture))
            {
                using var nroStream = new MemoryStream(blob, writable: false);
                return ReadNro(nroStream);
            }

            if (name.EndsWith(".nso", true, CultureInfo.InvariantCulture) || name.Equals("main", StringComparison.OrdinalIgnoreCase))
            {
                using var nsoStream = new MemoryStream(blob, writable: false);
                return ReadNso(nsoStream);
            }

            if (name.EndsWith(".nca", true, CultureInfo.InvariantCulture))
            {
                ncaCandidates.Add((name, blob));
            }
        }

        // Caminho comercial mínimo: tentar extrair Program NSO de NCAs já descriptografados/dumpados em formato legível.
        // Workaround isolado para UWP bring-up: não substitui pipeline NCA completo do upstream Eden.
        foreach (var nca in PrioritizeNcaCandidates(ncaCandidates))
        {
            if (TryExtractNsoFromNca(nca.Name, nca.Data, out RuntimeImage? image))
            {
                BootDiagnostics.Info($"Program NSO extraído de NCA: {nca.Name}");
                return image!;
            }
        }

        if (ncaCandidates.Count > 0)
        {
            string firstNca = ncaCandidates[0].Name;
            BootDiagnostics.Error($"NSP contém NCA ({firstNca}) mas nenhuma seção NSO legível foi encontrada. Necessário dump descriptografado/chaves corretas.");
            throw new InvalidDataException("NSP comercial detectado (NCA não legível para extração de Program NSO).");
        }

        throw new InvalidDataException("NSP sem conteúdo executável suportado encontrado.");
    }

    private static RuntimeImage ReadNro(Stream stream)
    {
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        stream.Position = 0x10;
        string magic = Encoding.ASCII.GetString(br.ReadBytes(4));
        if (!magic.Equals("NRO0", StringComparison.Ordinal)) throw new InvalidDataException("NRO inválido.");

        _ = br.ReadUInt32(); // version
        int nroSize = br.ReadInt32();
        _ = br.ReadInt32(); // flags

        int textOffset = br.ReadInt32();
        int textSize = br.ReadInt32();
        int roOffset = br.ReadInt32();
        int roSize = br.ReadInt32();
        int dataOffset = br.ReadInt32();
        int dataSize = br.ReadInt32();
        _ = br.ReadInt32(); // bssSize

        stream.Position = textOffset;
        byte[] text = br.ReadBytes(textSize);
        stream.Position = roOffset;
        byte[] ro = br.ReadBytes(roSize);
        stream.Position = dataOffset;
        byte[] data = br.ReadBytes(dataSize);

        if (stream.Length > nroSize)
        {
            long compressedPos = dataOffset + dataSize;
            if (compressedPos < stream.Length)
            {
                stream.Position = compressedPos;
                int compressedSize = (int)(stream.Length - stream.Position);
                if (compressedSize > 0)
                {
                    byte[] compressed = br.ReadBytes(compressedSize);
                    byte[] inflated = new byte[dataSize];
                    int decoded = LZ4Codec.Decode(compressed, 0, compressed.Length, inflated, 0, inflated.Length);
                    if (decoded > 0)
                    {
                        data = inflated;
                    }
                }
            }
        }

        return new RuntimeImage
        {
            EntryPoint = 0x0000007100000000,
            Text = text,
            Rodata = ro,
            Data = data
        };
    }


    private static IEnumerable<(string Name, byte[] Data)> PrioritizeNcaCandidates(List<(string Name, byte[] Data)> ncas)
    {
        return ncas
            .OrderByDescending(x => x.Name.Contains("program", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Data.Length);
    }

    private static bool TryExtractNsoFromNca(string ncaName, byte[] ncaData, out RuntimeImage? image)
    {
        image = null;

        // Estratégia pragmática: detectar NSO0 bruto dentro do blob NCA (caso dumps pré-processados).
        int nsoOffset = IndexOfAscii(ncaData, "NSO0");
        if (nsoOffset < 0)
        {
            BootDiagnostics.Warn($"NCA sem assinatura NSO0 visível: {ncaName}");
            return false;
        }

        try
        {
            using var nsoStream = new MemoryStream(ncaData, nsoOffset, ncaData.Length - nsoOffset, writable: false);
            image = ReadNso(nsoStream);
            return true;
        }
        catch (Exception ex)
        {
            BootDiagnostics.Warn($"Falha ao parsear NSO embutido em {ncaName}: {ex.Message}");
            return false;
        }
    }

    private static int IndexOfAscii(byte[] data, string pattern)
    {
        byte[] pat = Encoding.ASCII.GetBytes(pattern);
        for (int i = 0; i <= data.Length - pat.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pat.Length; j++)
            {
                if (data[i + j] != pat[j])
                {
                    match = false;
                    break;
                }
            }

            if (match) return i;
        }

        return -1;
    }

    private static RuntimeImage ReadNso(Stream stream)
    {
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        stream.Position = 0;
        string magic = Encoding.ASCII.GetString(br.ReadBytes(4));
        if (!magic.Equals("NSO0", StringComparison.Ordinal)) throw new InvalidDataException("NSO inválido.");

        _ = br.ReadUInt32(); // version
        _ = br.ReadUInt32(); // reserved
        uint flags = br.ReadUInt32();

        int textFileOff = br.ReadInt32();
        int textMemOff = br.ReadInt32();
        int textSize = br.ReadInt32();

        int roFileOff = br.ReadInt32();
        int roMemOff = br.ReadInt32();
        int roSize = br.ReadInt32();

        int dataFileOff = br.ReadInt32();
        int dataMemOff = br.ReadInt32();
        int dataSize = br.ReadInt32();

        _ = br.ReadInt32(); // bss
        _ = br.ReadInt32();
        _ = br.ReadInt32();
        _ = br.ReadInt32();

        int textCompSize = br.ReadInt32();
        int roCompSize = br.ReadInt32();
        int dataCompSize = br.ReadInt32();

        // skip build id and hashes

        byte[] text = ReadNsoSegment(stream, textFileOff, textCompSize, textSize, (flags & 1) != 0);
        byte[] ro = ReadNsoSegment(stream, roFileOff, roCompSize, roSize, (flags & 2) != 0);
        byte[] data = ReadNsoSegment(stream, dataFileOff, dataCompSize, dataSize, (flags & 4) != 0);

        BootDiagnostics.Info($"NSO loaded text={text.Length} ro={ro.Length} data={data.Length}");

        return new RuntimeImage
        {
            EntryPoint = 0x0000007100000000 + (ulong)textMemOff,
            Text = text,
            Rodata = ro,
            Data = data
        };
    }

    private static byte[] ReadNsoSegment(Stream stream, int fileOff, int compSize, int decompSize, bool compressed)
    {
        stream.Position = fileOff;
        byte[] src = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true).ReadBytes(compressed ? compSize : decompSize);
        if (!compressed) return src;

        byte[] dst = new byte[decompSize];
        int decoded = LZ4Codec.Decode(src, 0, src.Length, dst, 0, dst.Length);
        if (decoded <= 0) throw new InvalidDataException("Falha ao descomprimir segmento NSO LZ4.");
        return dst;
    }

    private static string ReadCString(byte[] bytes, int offset)
    {
        int i = offset;
        while (i < bytes.Length && bytes[i] != 0) i++;
        return Encoding.UTF8.GetString(bytes, offset, i - offset);
    }

    private static async Task<StorageFile?> TryGetFileAsync(StorageFolder folder, string fileName)
    {
        try
        {
            return await folder.GetFileAsync(fileName);
        }
        catch
        {
            return null;
        }
    }
}
