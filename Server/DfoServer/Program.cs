using DfoServer.Application;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
return await ServerApplication.RunAsync(args);
