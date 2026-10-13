using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Tests;

public class RoslynSymbolIndexTests
{
    [Fact]
    public async Task IndexWorkspaceAsync_DiscoversClassesInterfacesAndMethods()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-roslyn-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var code = @"
namespace DemoApp;

public interface IOrderService
{
    void ProcessOrder(int id);
}

public class OrderService : IOrderService
{
    public void ProcessOrder(int id)
    {
    }
}
";
            await File.WriteAllTextAsync(Path.Combine(tempDir, "OrderService.cs"), code);

            var indexer = new RoslynSymbolIndexProvider();
            var symbols = await indexer.IndexWorkspaceAsync(tempDir);

            Assert.NotEmpty(symbols);
            Assert.Contains(symbols, s => s.Kind == "Interface" && s.Name == "IOrderService");
            Assert.Contains(symbols, s => s.Kind == "Class" && s.Name == "OrderService");
            Assert.Contains(symbols, s => s.Kind == "Method" && s.Name == "OrderService.ProcessOrder");

            var classSymbol = symbols.First(s => s.Name == "OrderService" && s.Kind == "Class");
            Assert.True(classSymbol.StartLine > 0);
            Assert.True(classSymbol.EndLine >= classSymbol.StartLine);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task GetMatchingSymbolsPromptAsync_GeneratesXmlTagWithMatchingSymbols()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-roslyn-test2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var code = @"
namespace DemoApp;

public class PaymentGateway
{
    public void Charge() {}
}
";
            await File.WriteAllTextAsync(Path.Combine(tempDir, "PaymentGateway.cs"), code);

            var indexer = new RoslynSymbolIndexProvider();
            var prompt = await indexer.GetMatchingSymbolsPromptAsync(tempDir, "Please fix bug in PaymentGateway charge method", new LocalOptimizationFlags());

            Assert.Contains("<discovered_code_symbols>", prompt);
            Assert.Contains("Class PaymentGateway", prompt);
            Assert.Contains("PaymentGateway.cs", prompt);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
