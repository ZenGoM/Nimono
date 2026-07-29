using Microsoft.ML.OnnxRuntime;

namespace Nimono.Tests;

[TestClass]
public sealed class Test1
{
    [TestMethod]
    public void TestDirectMLExecutionProvider()
    {
        try
        {
            using var opts = new SessionOptions();
            opts.AppendExecutionProvider_DML(0);
            Assert.IsTrue(true, "DirectML Execution Provider was successfully appended.");
        }
        catch (Exception ex)
        {
            Assert.Fail($"DirectML Execution Provider failed to load: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
