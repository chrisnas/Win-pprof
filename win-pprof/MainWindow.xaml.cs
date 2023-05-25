using Perftools.Profiles;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;

namespace win_pprof
{
    public partial class MainWindow : Window
    {
        private const string AppTitle = ".pprof Viewer";
        private PProfFile _profile;
        private string _filename;

        public MainWindow()
        {
            InitializeComponent();

            // add copy to clipboard support for frames
            lvFunctions.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnFunctionsCopy, CanCopyFunctions));
        }

        void OnFunctionsCopy(object target, ExecutedRoutedEventArgs e)
        {
            CopyFunctions(e);
        }

        private void CopyFunctions(ExecutedRoutedEventArgs e)
        {
            var listview = e.OriginalSource as ListView;
            if (listview == null)
                return;

            var builder = new StringBuilder(1024);
            foreach (var item in listview.SelectedItems)
            {
                var function = item as Function;
                if (function == null)
                    return;

                builder.AppendLine(function.Name);
            }
            Clipboard.SetText(builder.ToString());
        }

        void CanCopyFunctions(object sender, CanExecuteRoutedEventArgs e)
        {
            var listview = e.OriginalSource as ListView;
            if (listview == null)
                return;

            e.CanExecute = (listview.SelectedItems.Count > 0);
        }

        private void OnFileOpen(object sender, ExecutedRoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.FileName = "Auto";
            dialog.DefaultExt = ".pprof";
            dialog.Filter = "Profile (.pprof)|*.pprof";

            bool? result = dialog.ShowDialog();
            if (result == true)
            {
                OpenFile(dialog.FileName);
            }
        }

        // Goto http://pinvoke.net for Win32 mappings
        //
        [DllImport("user32.dll")]   // http://msdn.microsoft.com/en-us/library/windows/desktop/ms647985(v=vs.85).aspx
        static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll")]   // http://msdn.microsoft.com/en-us/library/windows/desktop/ms647987(v=vs.85).aspx
        static extern bool InsertMenu(IntPtr hMenu, Int32 uPosition, Int32 uFlags, Int32 uIDNewItem, string lpNewItem);

        // Win32 constants to insert and handle menu notifications
        //
        const Int32 MF_BYPOSITION = 0x400;
        const Int32 MF_SEPARATOR = 0x800;
        const Int32 WM_SYSCOMMAND = 0x112;

        // numeric ID of our About menu
        //
        const Int32 IDM_ABOUT = 1000;

        private void CreateAboutSystemMenuItem()
        {
            // offset in the system menu
            const Int32 POS_BEFORE_CLOSE = 5;

            IntPtr hWnd = new WindowInteropHelper(this).Handle;
            IntPtr hSysMenu = GetSystemMenu(hWnd, false);

            // add our About command with a separator just before the last Close system command
            InsertMenu(hSysMenu, POS_BEFORE_CLOSE, MF_BYPOSITION, IDM_ABOUT, "About .pprof Viewer...");
            InsertMenu(hSysMenu, POS_BEFORE_CLOSE, MF_BYPOSITION | MF_SEPARATOR, -1, string.Empty);

            // subclass our MainWindow Window Procedure to be notified when the About item gets clicked
            HwndSource source = HwndSource.FromHwnd(hWnd);
            source.AddHook(new HwndSourceHook(SubclassedWindowProc));
        }
        static IntPtr SubclassedWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_SYSCOMMAND)
            {
                switch (wParam.ToInt32())
                {
                    case IDM_ABOUT:
                        AboutBox aboutBox = new AboutBox();
                        aboutBox.Owner = Application.Current.MainWindow;
                        aboutBox.ShowDialog();
                        handled = true;
                        break;
                }
            }

            return IntPtr.Zero;
        }

        private void UpdateTitle()
        {
            var name = System.Reflection.Assembly.GetExecutingAssembly().GetName();
            var titleBuilder = new StringBuilder();
            titleBuilder.AppendFormat("{0} (v{1}.{2}.{3})", AppTitle, name.Version.Major, name.Version.Minor, name.Version.Build);

            if (!string.IsNullOrEmpty(_filename))
            {
                titleBuilder.AppendFormat("  -  {0}", _filename);
                if (_profile != null)
                {
                    TimeSpan ts = new TimeSpan(0, 0, 0, 0, (int)(_profile.DurationNS / 1000000));
                    titleBuilder.AppendFormat($" [{ts.TotalSeconds} s]");
                }
            }

            Title = titleBuilder.ToString();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateTitle();

            // add "About" item in the System menu
            CreateAboutSystemMenuItem();
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Note that you can have more than one file.
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                var file = files[0];
                OpenFile(file);
            }
        }

        public void OpenFile(string filename)
        {
            try
            {
                using var s = File.OpenRead(filename);
                var profile = Profile.Parser.ParseFrom(s);
                _profile = new PProfFile();
                if (!_profile.Load(profile))
                {
                    MessageBox.Show($"Error while loading {filename}");
                    return;
                }

                // change data context for bindings
                //DataContext = _profile;
                tabMappings.Header = $"Mappings ({_profile.Mappings.Count()})";
                lvMappings.ItemsSource = _profile.Mappings;
                lvMappings.SelectedIndex = 0;

                tabLocations.Header = $"Locations ({_profile.Locations.Count()})";
                lvLocations.ItemsSource = _profile.Locations;
                lvLocations.SelectedIndex = 0;

                tabFunctions.Header = $"Functions ({_profile.Functions.Count()})";
                lvFunctions.ItemsSource = _profile.Functions;
                ApplyFunctionFilter();
                lvFunctions.SelectedIndex = 0;

                ctrlSamples.Update(_profile);
                tabSamples.Header = $"Samples ({_profile.Samples.Count()})";

                _filename = filename;
                UpdateTitle();
            }
            catch (Exception x)
            {
                MessageBox.Show($"Error while loading {filename}: {x.Message}");
            }
        }


#region Functions
#endregion
        private void OnFilterFunctionTextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFunctionFilter();
        }

        private void ApplyFunctionFilter()
        {
            var view = (ListCollectionView)CollectionViewSource.GetDefaultView(lvFunctions.ItemsSource);
            if (view == null)
            {
                return;
            }

            var filterString = tbFilterFunctions.Text;
            if (string.IsNullOrEmpty(filterString))
            {
                view.Filter = null;
                return;
            }

            view.Filter = delegate (object item)
            {
                var function = (Function)item;
                return function.Name.Contains(filterString);
            };
        }

#region Locations
#endregion


    }
}
