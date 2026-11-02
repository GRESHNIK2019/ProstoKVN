using System;
using System.IO;
using System.Threading;
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "started.fixture"), DateTime.UtcNow.ToString("O"));
Thread.Sleep(6200);
