using Csl.Demo;

// Csl.Demo                              the shaders of Shaders/ in a window, redrawn from their C# on each save
// Csl.Demo --shot FILE.png [--time T] [--blur R]
//                                       compile them from their files, draw once, save the image, exit (hidden); R 0: no blur
// Csl.Demo --cpu-check DIR [--time T]   draw every demo on the GPU and on the CPU (Csl.Cpu), compare, write the images
//                                       and the report to DIR, exit (hidden); the exit code is the number that differ
// Csl.Demo --cpu [--time T] [--shot FILE.png]
//                                       the same, every shader run by the CPU (Csl.Cpu): frames computed in the
//                                       background and drawn as they come (fps in the title); with --shot, one at T
// Csl.Demo --cpu-bench N [--time T]     N CPU frames one after the other, their cost and the median fps (hidden)
// Csl.Demo --debug-pixel NAME X Y [--time T]
//                                       one pixel of a demo on the CPU (320x180), no GPU: under a debugger it stops
//                                       right before the pixel, F11 steps into the shader's C#
string? shot = null;
string? cpuCheck = null;
(string Name, int X, int Y)? debugPixel = null;
bool onCpu = args.Contains("--cpu");
int cpuBench = 0;
float time = 2.0f;
int? blur = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--shot")
        shot = Path.GetFullPath(args[i + 1]);
    else if (args[i] == "--debug-pixel" && i + 3 < args.Length)
        debugPixel = (args[i + 1], int.Parse(args[i + 2]), int.Parse(args[i + 3]));
    else if (args[i] == "--cpu-bench")
        cpuBench = int.Parse(args[i + 1]);
    else if (args[i] == "--cpu-check")
        cpuCheck = Path.GetFullPath(args[i + 1]);
    else if (args[i] == "--time")
        time = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
    else if (args[i] == "--blur")
        blur = int.Parse(args[i + 1]);
}
if (debugPixel is { } pixel)
    return CpuCheck.DebugPixel(pixel.Name, pixel.X, pixel.Y, time);
using var game = new DemoGame(shot, time, blur, cpuCheck, onCpu, cpuBench);
game.Run();
return game.CpuCheckFailures;
