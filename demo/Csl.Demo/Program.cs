using Csl.Demo;

// Csl.Demo                              the shaders of Shaders/ in a window, redrawn from their C# on each save
// Csl.Demo --shot FILE.png [--time T]   compile them from their files, draw once, save the image, exit (hidden)
string? shot = null;
float time = 2.0f;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--shot")
        shot = Path.GetFullPath(args[i + 1]);
    else if (args[i] == "--time")
        time = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
}
using var game = new DemoGame(shot, time);
game.Run();
