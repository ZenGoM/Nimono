using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Nimono.Tests;

[TestClass]
public class MainFormTests
{
    [TestMethod]
    public void RenderGroups_DisplaysGroupPanels_InResultsPanel()
    {
        Exception? threadException = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm();

                var imageGroupType = typeof(MainForm).Assembly.GetType("Nimono.ImageGroup");
                Assert.IsNotNull(imageGroupType, "ImageGroup type should exist in Nimono assembly.");

                var ctor = imageGroupType.GetConstructor(new[] { typeof(int), typeof(IReadOnlyList<string>) });
                Assert.IsNotNull(ctor, "ImageGroup constructor not found.");

                var paths = new List<string> { "C:\\temp\\a.jpg", "C:\\temp\\b.jpg" };
                var group = ctor.Invoke(new object[] { 1, paths.AsReadOnly() });

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

                var header = groupPanel.Controls.OfType<Label>().FirstOrDefault();
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

        if (threadException is not null)
            Assert.Fail($"Test thread failed: {threadException}", threadException);
    }

    [TestMethod]
    public void RenderGroups_SetsHeightForAllGroupPanels()
    {
        Exception? threadException = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm();

                var imageGroupType = typeof(MainForm).Assembly.GetType("Nimono.ImageGroup");
                Assert.IsNotNull(imageGroupType, "ImageGroup type should exist in Nimono assembly.");

                var ctor = imageGroupType.GetConstructor(new[] { typeof(int), typeof(IReadOnlyList<string>) });
                Assert.IsNotNull(ctor, "ImageGroup constructor not found.");

                var group1Paths = new List<string> { "C:\\temp\\a1.jpg", "C:\\temp\\b1.jpg" };
                var group2Paths = new List<string> { "C:\\temp\\a2.jpg", "C:\\temp\\b2.jpg" };
                var group1 = ctor.Invoke(new object[] { 1, group1Paths.AsReadOnly() });
                var group2 = ctor.Invoke(new object[] { 2, group2Paths.AsReadOnly() });

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

        if (threadException is not null)
            Assert.Fail($"Test thread failed: {threadException}", threadException);
    }
}
