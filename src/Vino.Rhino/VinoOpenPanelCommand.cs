using Vino.BridgeContract;
using Rhino.Commands;
using Rhino.UI;

namespace Vino.Rhino;

public sealed class VinoOpenPanelCommand : Command
{
    public override string EnglishName => "VinoOpenPanel";

    protected override Result RunCommand(global::Rhino.RhinoDoc document, RunMode mode)
    {
        DevelopmentDiagnosticTrace.TryWrite(
            "Rhino",
            "open-panel-command",
            $"serial={document?.RuntimeSerialNumber ?? 0};saved={document is not null && !string.IsNullOrWhiteSpace(document.Path)}");
        if (document is null || string.IsNullOrWhiteSpace(document.Path))
        {
            global::Rhino.RhinoApp.WriteLine(
                "Vino requires a saved Rhino document before opening its panel.");
            return Result.Nothing;
        }

        VinoRuntimeHost.Instance.ObserveRhinoDocument(document.RuntimeSerialNumber);
        Panels.OpenPanel(typeof(VinoPanel), true);
        var result = Panels.IsPanelVisible(typeof(VinoPanel))
            ? Result.Success
            : Result.Failure;
        DevelopmentDiagnosticTrace.TryWrite("Rhino", "open-panel-result", result.ToString());
        return result;
    }
}
