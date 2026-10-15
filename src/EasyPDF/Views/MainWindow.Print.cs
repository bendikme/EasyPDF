using System.Windows.Interop;
using System.Windows.Threading;
using EasyPDF.Resources;
using EasyPDF.Services;

namespace EasyPDF.Views;

public partial class MainWindow
{
    private void Print()
    {
        if (_document is not { } document) return;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var path = document.FilePath;
            PrintService.Print(handle, document, _password, Math.Max(0, View.CurrentPage),
                message => Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
                {
                    if (!_closed) ShowToast(message);
                }),
                result => DescribeFailure(result, path));
        }
        catch (Exception ex)
        {
            AppLog.Error("The print dialog could not be shown.", ex);
            ShowNotice(Strings.Print_Failed_Title, UiLanguage.Format(Strings.Print_Failed, ex.Message));
        }
    }
}
