using Csl.Debugging;
using Stride.Engine;

using var game = new Game();
CslDebug.Register(game);
game.Run();
