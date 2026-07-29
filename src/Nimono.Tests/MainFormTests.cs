using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Nimono.Tests;

[TestClass]
public class MainFormTests
{
    private static string CreateTempImageFile()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".jpg");
        File.WriteAllBytes(path, new byte[100]);
        return path;
    }

    [TestMethod]
    public void RenderGroups_DisplaysGroupPanels_InResultsPanel()
    {
        Exception? threadException = null;
        var tempFiles = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm();

                var imageGroupType = typeof(MainForm).Assembly.GetType("Nimono.ImageGroup");
                Assert.IsNotNull(imageGroupType, "ImageGroup type should exist in Nimono assembly.");

                var ctor = imageGroupType.GetConstructor(new[] {
                    typeof(int),
                    typeof(IReadOnlyList<string>),
                    typeof(IReadOnlyDictionary<string, double>),
                    typeof(IReadOnlyDictionary<string, ulong>),
                    typeof(IReadOnlyDictionary<string, float[]>)
                });
                Assert.IsNotNull(ctor, "ImageGroup constructor not found.");

                string file1 = CreateTempImageFile();
                string file2 = CreateTempImageFile();
                tempFiles.Add(file1);
                tempFiles.Add(file2);

                var paths = new List<string> { file1, file2 };
                var similarities = new Dictionary<string, double> { { file1, 1.0 }, { file2, 0.9 } };
                var hashes = new Dictionary<string, ulong> { { file1, 0UL }, { file2, 0UL } };
                var group = ctor.Invoke(new object[] { 1, paths.AsReadOnly(), similarities, hashes, null! });

                var groupsArray = Array.CreateInstance(imageGroupType, 1);
                groupsArray.SetValue(group, 0);

                var renderMethod = typeof(MainForm).GetMethod("RenderGroups", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(renderMethod, "RenderGroups method should exist.");

                renderMethod.Invoke(form, new object[] { groupsArray });

                var resultsPanelField = typeof(MainForm).GetField("_resultsPanel", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(resultsPanelField, "_resultsPanel field should exist.");

                var resultsPanel = (Panel)resultsPanelField.GetValue(form)!;
                Assert.AreEqual(1, resultsPanel.Controls.Count, "Results panel should contain one group panel.");

                var groupPanel = resultsPanel.Controls[0];
                Assert.IsInstanceOfType(groupPanel, typeof(Panel), "Group panel should be a Panel.");

                var header = groupPanel.Controls.OfType<Panel>()
                    .SelectMany(p => p.Controls.OfType<Label>())
                    .FirstOrDefault();
                Assert.IsNotNull(header, "Group panel should contain a header label.");
                Assert.AreEqual("グループ 1 — 2 枚", header.Text);
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        foreach (var file in tempFiles)
        {
            try { File.Delete(file); } catch { }
        }

        if (threadException is not null)
            Assert.Fail($"Test thread failed: {threadException}", threadException);
    }

    [TestMethod]
    public void RenderGroups_SetsHeightForAllGroupPanels()
    {
        Exception? threadException = null;
        var tempFiles = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm();

                var imageGroupType = typeof(MainForm).Assembly.GetType("Nimono.ImageGroup");
                Assert.IsNotNull(imageGroupType, "ImageGroup type should exist in Nimono assembly.");

                var ctor = imageGroupType.GetConstructor(new[] {
                    typeof(int),
                    typeof(IReadOnlyList<string>),
                    typeof(IReadOnlyDictionary<string, double>),
                    typeof(IReadOnlyDictionary<string, ulong>),
                    typeof(IReadOnlyDictionary<string, float[]>)
                });
                Assert.IsNotNull(ctor, "ImageGroup constructor not found.");

                string fileA1 = CreateTempImageFile();
                string fileB1 = CreateTempImageFile();
                string fileA2 = CreateTempImageFile();
                string fileB2 = CreateTempImageFile();
                tempFiles.Add(fileA1);
                tempFiles.Add(fileB1);
                tempFiles.Add(fileA2);
                tempFiles.Add(fileB2);

                var group1Paths = new List<string> { fileA1, fileB1 };
                var group1Sim = new Dictionary<string, double> { { fileA1, 1.0 }, { fileB1, 0.9 } };
                var group1Hash = new Dictionary<string, ulong> { { fileA1, 0UL }, { fileB1, 0UL } };
                var group2Paths = new List<string> { fileA2, fileB2 };
                var group2Sim = new Dictionary<string, double> { { fileA2, 1.0 }, { fileB2, 0.9 } };
                var group2Hash = new Dictionary<string, ulong> { { fileA2, 0UL }, { fileB2, 0UL } };

                var group1 = ctor.Invoke(new object[] { 1, group1Paths.AsReadOnly(), group1Sim, group1Hash, null! });
                var group2 = ctor.Invoke(new object[] { 2, group2Paths.AsReadOnly(), group2Sim, group2Hash, null! });

                var groupsArray = Array.CreateInstance(imageGroupType, 2);
                groupsArray.SetValue(group1, 0);
                groupsArray.SetValue(group2, 1);

                var renderMethod = typeof(MainForm).GetMethod("RenderGroups", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(renderMethod, "RenderGroups method should exist.");

                renderMethod.Invoke(form, new object[] { groupsArray });

                var resultsPanelField = typeof(MainForm).GetField("_resultsPanel", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(resultsPanelField, "_resultsPanel field should exist.");

                var resultsPanel = (Panel)resultsPanelField.GetValue(form)!;
                Assert.AreEqual(2, resultsPanel.Controls.Count, "Results panel should contain two group panels.");

                resultsPanel.PerformLayout();

                var firstPanel = resultsPanel.Controls[0];
                var secondPanel = resultsPanel.Controls[1];

                Assert.IsTrue(firstPanel.Height > 0, "First group panel height should be positive.");
                Assert.IsTrue(secondPanel.Height > 0, "Second group panel height should be positive.");
                Assert.IsTrue(secondPanel.Top >= firstPanel.Top + firstPanel.Height + 8,
                    "Second group panel should be positioned below the first with appropriate spacing.");
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        foreach (var file in tempFiles)
        {
            try { File.Delete(file); } catch { }
        }

        if (threadException is not null)
            Assert.Fail($"Test thread failed: {threadException}", threadException);
    }
}
