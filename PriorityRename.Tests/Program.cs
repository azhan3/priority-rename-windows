using PriorityRename;

static class TestRunner
{
    private static int failures;

    public static void Main()
    {
        TestCascade();
        TestContiguousGap();
        TestUndo();
        TestLockedFile();

        if (failures != 0)
        {
            Console.Error.WriteLine($"{failures} test(s) failed.");
            Environment.Exit(1);
        }
        Console.WriteLine("All rename engine tests passed.");
    }

    private static void TestCascade()
    {
        var directory = CreateTempDirectory();
        try
        {
            var source = Path.Combine(directory, "draft.pdf");
            File.WriteAllText(source, "draft");
            File.WriteAllText(Path.Combine(directory, "report.pdf"), "1");
            File.WriteAllText(Path.Combine(directory, "report (2).pdf"), "2");

            var result = RenameEngine.Rename(source, "report.pdf", true);
            Assert(result.Success, "Cascade rename should succeed.");
            Assert(File.ReadAllText(source.Replace("draft", "report")) == "draft", "Source should receive report.pdf.");
            Assert(File.ReadAllText(Path.Combine(directory, "report (2).pdf")) == "1", "Old report.pdf should shift.");
            Assert(File.ReadAllText(Path.Combine(directory, "report (3).pdf")) == "2", "Old report (2).pdf should shift.");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void TestContiguousGap()
    {
        var directory = CreateTempDirectory();
        try
        {
            var source = Path.Combine(directory, "draft.pdf");
            File.WriteAllText(source, "draft");
            File.WriteAllText(Path.Combine(directory, "report.pdf"), "1");
            File.WriteAllText(Path.Combine(directory, "report (3).pdf"), "3");

            var result = RenameEngine.Rename(source, "report.pdf", true);
            Assert(result.Success, "Gap rename should succeed.");
            Assert(File.Exists(Path.Combine(directory, "report.pdf")), "Source should receive report.pdf.");
            Assert(File.Exists(Path.Combine(directory, "report (2).pdf")), "report.pdf should shift.");
            Assert(File.Exists(Path.Combine(directory, "report (3).pdf")), "Gap file should remain untouched.");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void TestUndo()
    {
        var directory = CreateTempDirectory();
        try
        {
            var source = Path.Combine(directory, "draft.pdf");
            File.WriteAllText(source, "draft");
            File.WriteAllText(Path.Combine(directory, "report.pdf"), "1");
            var result = RenameEngine.Rename(source, "report.pdf", true);
            Assert(result.Success, "Setup rename should succeed.");

            var undo = RenameEngine.Undo(result.Mappings);
            Assert(undo.Success, "Undo should succeed.");
            Assert(File.ReadAllText(source) == "draft", "Source should return to its original name.");
            Assert(File.ReadAllText(Path.Combine(directory, "report.pdf")) == "1", "Existing file should return to report.pdf.");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void TestLockedFile()
    {
        var directory = CreateTempDirectory();
        try
        {
            var source = Path.Combine(directory, "draft.pdf");
            var report = Path.Combine(directory, "report (2).pdf");
            File.WriteAllText(source, "draft");
            File.WriteAllText(Path.Combine(directory, "report.pdf"), "1");
            File.WriteAllText(report, "2");

            using var locked = new FileStream(report, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var result = RenameEngine.Rename(source, "report.pdf", true);
            Assert(!result.Success, "Locked conflict should abort.");
            Assert(File.Exists(source), "Source should remain.");
            Assert(File.Exists(Path.Combine(directory, "report.pdf")), "Target should remain.");
            Assert(File.Exists(report), "Locked file should remain.");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PriorityRenameTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            Console.Error.WriteLine("FAIL: " + message);
            failures++;
        }
    }
}
