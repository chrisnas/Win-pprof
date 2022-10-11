using System.Reflection;
using System.Windows;

namespace win_pprof {


public partial class AboutBox : Window
{
    public AboutBox()
    {
        InitializeComponent();

        // show the version in the title
        var name = Assembly.GetExecutingAssembly().GetName();
        Title = string.Format("{0} - v{1}.{2}", Title, name.Version.Major, name.Version.Minor);
    }
}



}
