using System.Windows;
using EasyPDF.Pdf;
using EasyPDF.Prc;
using EasyPDF.Resources;
using EasyPDF.Services;
using EasyPDF.Viewer;

namespace EasyPDF.Views;

public partial class MainWindow
{
    /// <summary>The 3D view over the pages, built the first time a model is shown.</summary>
    private ModelPanel? _modelPanel;

    /// <summary>The model shown, and the page being read when it was opened: going to another closes it.</summary>
    private ModelTarget? _modelShown;

    private int _modelPage = -1;
    private int _modelGeneration;

    /// <summary>
    /// The last model read, held only as long as nothing else needs the memory: shown again soon
    /// after it was closed, it is there at once.
    /// </summary>
    private (PdfDocument Document, ModelTarget Target, WeakReference<LoadedModel> Model)? _lastModel;

    private sealed record LoadedModel(ModelRenderer Renderer, IReadOnlyList<(string Name, ModelCamera Camera)> Views, string? Note);

    internal bool IsModelShown => _modelShown is not null;

    /// <summary>The 3D view, once built. For tests.</summary>
    internal ModelPanel? ModelPanel => _modelPanel;

    /// <summary>
    /// Shows a 3D annotation's model over the pages: read from the file on PDFium's thread, its
    /// geometry decoded on the thread pool - nothing of it is touched before it is asked for.
    /// </summary>
    private async void ShowModel(ModelTarget target)
    {
        if (_document is not { } document || _closed) return;
        var generation = ++_modelGeneration;
        var panel = _modelPanel;
        if (panel is null)
        {
            panel = _modelPanel = new ModelPanel(this);
            panel.CloseRequested += (_, _) => CloseModel();
            ViewerArea.Children.Insert(ViewerArea.Children.IndexOf(LoadingText), panel);
        }
        _modelShown = target;
        _modelPage = View.CurrentPage;
        panel.Visibility = Visibility.Visible;

        if (_lastModel is { } last && ReferenceEquals(last.Document, document) && last.Target == target && last.Model.TryGetTarget(out var kept))
        {
            panel.ShowModel(kept.Renderer, kept.Views, kept.Note);
            return;
        }

        panel.ShowReading();
        string? failure = null;
        LoadedModel? loaded = null;
        try
        {
            var model = await document.LoadModelAsync(target).ConfigureAwait(true);
            if (generation != _modelGeneration) return;
            if (model is null) failure = Strings.Model_Failed;
            else if (model.Kind != "PRC") failure = Strings.Model_U3D;
            else
            {
                loaded = await Task.Run(() => Load(model)).ConfigureAwait(true);
                if (loaded is null) failure = Strings.Model_Empty;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"The 3D model on page {target.PageIndex + 1} of {document.FileName} could not be shown.", ex);
            failure = Strings.Model_Failed;
        }
        if (generation != _modelGeneration || _closed) return;

        if (loaded is null)
        {
            panel.ShowFailure(failure ?? Strings.Model_Failed);
            return;
        }
        _lastModel = (document, target, new WeakReference<LoadedModel>(loaded));
        panel.ShowModel(loaded.Renderer, loaded.Views, loaded.Note);
    }

    /// <summary>A PRC model, decoded and placed, with the cameras of its views - or, with none, one that shows it all. Null when it draws nothing.</summary>
    private static LoadedModel? Load(Pdf3DModel model)
    {
        var scene = ModelScene.Read(PrcFile.Read(model.Data));
        if (scene.TriangleCount == 0 && scene.LineCount == 0) return null;
        var views = new List<(string, ModelCamera)>();
        foreach (var view in model.Views)
            if (ModelCamera.FromView(view.CameraToWorld, view.FieldOfView, model.Aspect) is { } camera) views.Add((view.Name, camera));
        if (views.Count == 0) views.Add((string.Empty, ModelCamera.Overview(scene.Min, scene.Max)));
        var note = scene.PartsLeftOut > 0 ? UiLanguage.Format(Strings.Model_LeftOut, scene.PartsLeftOut) : null;
        return new LoadedModel(new ModelRenderer(scene), views, note);
    }

    /// <summary>Back to the pages from the 3D view.</summary>
    internal void CloseModel()
    {
        if (_modelShown is null) return;
        _modelGeneration++;
        _modelShown = null;
        _modelPage = -1;
        if (_modelPanel is { } panel)
        {
            panel.Release();
            panel.Visibility = Visibility.Collapsed;
        }
        if (_document is not null && !_closed) View.Focus();
    }

    /// <summary>The 3D view stays while the page it was opened from is the one being read.</summary>
    private void CloseModelIfPageChanged()
    {
        if (_modelShown is not null && _document is not null && View.CurrentPage != _modelPage) CloseModel();
    }
}
