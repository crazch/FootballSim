using System;
using FootballSim.Tests;

class Program
{
    static void Main()
    {
        Console.WriteLine("Running SystemsTests...");

        SystemsTests.RunAll();

        Console.WriteLine("Done.");
    }
}