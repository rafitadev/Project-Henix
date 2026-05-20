using Eden.UwpPorting.Core;
using K4os.Compression.LZ4;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
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
            throw new FileNotFoundException("prod.keys não encontrado em LocalFolder/keys.");
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

        byte[] magic = reader.ReadBytes(4);
        stream.Position = 0;

        string textMagic = Encoding.ASCII.GetString(magic);
        return textMagic switch
        {
            "NRO0" => ReadNro(stream),
            "PFS0" => ReadFromPfs0(stream),
            "HEAD" => ReadFromXci(stream),
            _ => throw new InvalidDataException($"Formato não suportado: {textMagic}")
        };
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
        foreach (var e in entries)
        {
            string name = ReadCString(stringTable, e.NameOffset);
            if (!name.EndsWith(".nsp", true, CultureInfo.InvariantCulture)) continue;
            long filePos = 0x100 + 0x10 + fileCount * 0x40 + stringTableSize + e.Offset;
            stream.Position = filePos;
            byte[] nsp = br.ReadBytes((int)e.Size);
            using var nspStream = new MemoryStream(nsp, writable: false);
            return ReadFromPfs0(nspStream);
        }

        throw new InvalidDataException("XCI sem partição NSP encontrada.");
    }

    private static RuntimeImage ReadFromPfs0(Stream stream)
    {
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        string magic = Encoding.ASCII.GetString(br.ReadBytes(4));
        if (!magic.Equals("PFS0", StringComparison.Ordinal)) throw new InvalidDataException("Container NSP inválido.");

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

        byte[] stringTable = br.ReadBytes(stringTableSize);
        long dataOffset = 0x10 + fileCount * 0x18 + stringTableSize;

        foreach (var e in entries)
        {
            string name = ReadCString(stringTable, e.NameOffset);
            if (!name.EndsWith(".nro", true, CultureInfo.InvariantCulture)) continue;

            stream.Position = dataOffset + e.Offset;
            byte[] nro = br.ReadBytes((int)e.Size);
            using var nroStream = new MemoryStream(nro, writable: false);
            return ReadNro(nroStream);
        }

        throw new InvalidDataException("NSP sem conteúdo NRO suportado encontrado.");
    }

    private static RuntimeImage ReadNro(Stream stream)
    {
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        stream.Position = 0;
        _ = br.ReadUInt32();
        _ = br.ReadUInt32();
        _ = br.ReadUInt32();
        _ = br.ReadUInt32();
        string magic = Encoding.ASCII.GetString(br.ReadBytes(4));
        if (!magic.Equals("NRO0", StringComparison.Ordinal)) throw new InvalidDataException("NRO inválido.");

        _ = br.ReadUInt32();
        int nroSize = br.ReadInt32();
        _ = br.ReadInt32();
        int textOffset = br.ReadInt32();
        int textSize = br.ReadInt32();
        int roOffset = br.ReadInt32();
        int roSize = br.ReadInt32();
        int dataOffset = br.ReadInt32();
        int dataSize = br.ReadInt32();
        int bssSize = br.ReadInt32();
        _ = bssSize;

        stream.Position = textOffset;
        byte[] text = br.ReadBytes(textSize);
        stream.Position = roOffset;
        byte[] ro = br.ReadBytes(roSize);
        stream.Position = dataOffset;
        byte[] data = br.ReadBytes(dataSize);

        if (nroSize < dataOffset + dataSize && nroSize > 0)
        {
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

        return new RuntimeImage
        {
            EntryPoint = 0x7100000000,
            Text = text,
            Rodata = ro,
            Data = data
        };
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
