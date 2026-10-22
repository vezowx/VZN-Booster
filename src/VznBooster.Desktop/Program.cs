using System;
using Velopack;

namespace VznBooster.Desktop;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();   // tem que ser a primeira linha

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}