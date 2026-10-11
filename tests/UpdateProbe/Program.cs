// Headless executable used only in isolated updater tests.
if (args.Length == 2 && args[0] == "--update-ready")
{
    if (File.Exists(Path.Combine(AppContext.BaseDirectory, "fail-update"))) return 1;
    File.WriteAllText(args[1] + ".tmp", typeof(Program).Assembly.GetName().Version!.ToString(3));
    File.Move(args[1] + ".tmp", args[1], true);
}
else File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "rollback-started"), "ok");
return 0;
