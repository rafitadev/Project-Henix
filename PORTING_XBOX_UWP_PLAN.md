# Eden -> Xbox Series X (UWP Dev Mode) Porting Plan

## Objetivo inicial
Foco total em **BOOT e compatibilidade**. O primeiro marco é carregar keys, montar ROM, inicializar CPU em interpretador, iniciar GPU D3D12 e áudio, mesmo com FPS baixo.

## 1) Projeto/Target UWP
- Criar projeto dedicado `Eden.UwpPorting` com `TargetFramework` `uap10.0.19041`.
- Definir constantes de compilação para bloquear caminhos de JIT e forçar backend de vídeo D3D12:
  - `CPU_INTERPRETER_ONLY`
  - `GPU_D3D12`
- Manter o core do Eden em assemblies compartilháveis (`netstandard2.0` quando possível) e criar *adapters* UWP somente na borda.

## 2) CPU sem JIT (compliance UWP)
- Em UWP Xbox, evitar geração de código dinâmico/exec memory no caminho inicial.
- Forçar `InterpreterOnly` no bootstrap.
- Travar qualquer fallback para JIT com erro explícito em runtime.

## 3) Renderização
- Criar `IGraphicsBackend` e implementar `D3D12GraphicsBackend`.
- Estratégia de transição:
  1. Traduzir comandos de alto nível do scheduler GPU para command lists D3D12.
  2. Criar pipeline cache equivalente ao backend atual.
  3. Adicionar tradutor de shader para DXIL/HLSL (fase 2).

## 4) VFS / Storage sandbox UWP
- Usar `ApplicationData.Current.LocalFolder` para `keys/` e cache.
- Usar `FileOpenPicker` para importar `.xci/.nsp`.
- Persistir `FutureAccessList` para reabrir arquivos entre sessões.

## 5) Input e áudio
- Input via `Windows.Gaming.Input.Gamepad` com tradução de layout Xbox->Switch.
- Áudio via `AudioGraph` (`GameMedia`) com `AudioFrameInputNode`.

## Ordem de implementação (milestone BOOT)
1. Boot app UWP e janela.
2. VFS + keys.
3. CPU InterpreterOnly.
4. GPU backend stub D3D12 + present loop.
5. Input/áudio básicos.
6. Boot de homebrew simples (NRO pequeno) e coleta de logs.
