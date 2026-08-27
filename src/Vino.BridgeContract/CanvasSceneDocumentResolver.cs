namespace Vino.CanvasSceneAdapter;

/// <summary>
/// Explicit document resolution for the canvas and Rhino-scene adapters. Lives in the
/// bridge-contract assembly because BOTH domain assemblies consume it; the namespace is the
/// adapters' own, kept across the assembly split so no consumer changed.
/// </summary>
public interface ICanvasSceneDocumentResolver<out TDocument>
    where TDocument : class
{
    TDocument Resolve(DocumentTarget target);
}
