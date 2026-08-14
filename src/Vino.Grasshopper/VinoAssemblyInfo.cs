using System.Drawing;
using Vino.BridgeContract;
using Vino.CanvasSceneAdapter;
using Vino.ScriptAdapter;
using Grasshopper.Kernel;

namespace Vino.Grasshopper;

public sealed class VinoAssemblyInfo : GH_AssemblyInfo
{
    private static Bitmap? _icon;

    public override string Name => "Vino";

    public override Bitmap Icon => _icon ??= LoadAssemblyIcon();

    private static Bitmap LoadAssemblyIcon()
    {
        using var stream = typeof(VinoAssemblyInfo).Assembly
            .GetManifestResourceStream("Vino.Grasshopper.AssemblyIcon.png");
        return stream is null ? null! : new Bitmap(stream);
    }

    public override string Description =>
        "Document-bound Grasshopper bridge for the Vino modeling orchestrator.";

    public override Guid Id => new("d2b0c9b2-f64b-4be7-98fc-f01590e88ac8");

    public override string AuthorName => "Vino contributors";

    public override string AuthorContact => "https://github.com/hongikarchi/Vino";
}

public sealed class VinoAssemblyPriority : GH_AssemblyPriority
{
    public override GH_LoadingInstruction PriorityLoad()
    {
        DevelopmentDiagnosticTrace.TryWrite("Grasshopper", "priority-load-enter");
        try
        {
            GrasshopperDocumentCatalog.Initialize();
            GrasshopperSelectionWatcher.Start();
            var resolver = new ExplicitGrasshopperDocumentResolver();
            BridgeProcessHub.RegisterOperationHandler(
                new CanvasBridgeOperationHandler(
                    new GrasshopperCanvasFoundationAdapter(resolver)));
            BridgeProcessHub.RegisterOperationHandler(
                new ScriptBridgeOperationHandler(
                    new GrasshopperPythonFoundationAdapter(resolver)));
            var documentCount = global::Grasshopper.Instances.DocumentServer.DocumentCount;
            var hasActiveCanvas = global::Grasshopper.Instances.ActiveCanvas is not null;
            DevelopmentDiagnosticTrace.TryWrite(
                "Grasshopper",
                "priority-load-ready",
                $"documents={documentCount};activeCanvas={hasActiveCanvas}");
            return GH_LoadingInstruction.Proceed;
        }
        catch (Exception exception)
        {
            GrasshopperSelectionWatcher.Stop();
            GrasshopperDocumentCatalog.Teardown();
            DevelopmentDiagnosticTrace.TryWriteException(
                "Grasshopper",
                "priority-load-failed",
                exception);
            throw;
        }
    }
}
