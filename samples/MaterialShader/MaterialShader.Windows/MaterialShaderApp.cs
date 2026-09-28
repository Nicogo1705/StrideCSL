using Csl.Debugging;
using Stride.Engine;

using var game = new Game();
// Ctrl+click a pixel: its mesh run on the CPU, stopping in the debugger; the C# shaders reloaded on save.
CslDebug.Register(game, "../MaterialShader.Game/Shaders");
game.Run();
