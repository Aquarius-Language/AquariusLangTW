using AquariusLang.Desktop.runtime;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
System.Environment.ExitCode = CommandLine.Run(args);
