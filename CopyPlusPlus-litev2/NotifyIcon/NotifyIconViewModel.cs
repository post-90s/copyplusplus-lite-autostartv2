using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace CopyPlusPlus.NotifyIcon
{
    /// <summary>
    ///     Provides bindable properties and commands for the NotifyIcon. In this sample, the
    ///     view model is assigned to the NotifyIcon in XAML. Alternatively, the startup routing
    ///     in App.xaml.cs could have created this view model, and assigned it to the NotifyIcon.
    /// </summary>
    public class NotifyIconViewModel
    {
        private static MainWindow GetMainWindow()
        {
            return Application.Current.Windows
                .OfType<MainWindow>()
                .FirstOrDefault();
        }

        public bool AutoStartEnabled
        {
            get => AutoStartManager.GetEnabled();
            set => AutoStartManager.SetEnabled(value);
        }

        /// <summary>
        ///     Shows a window, if none is already open.
        /// </summary>
        public ICommand ShowWindowCommand
        {
            get
            {
                return new DelegateCommand
                {
                    CanExecuteFunc = () => Application.Current.MainWindow != null,
                    CommandAction = () =>
                    {
                        //Application.Current.MainWindow = new MainWindow();
                        if (Application.Current.MainWindow != null)
                        {
                            Application.Current.MainWindow.ShowInTaskbar = true;
                            Application.Current.MainWindow.Show();
                            Application.Current.MainWindow.WindowState = WindowState.Normal;
                            Application.Current.MainWindow.Activate();
                        }

                        GetMainWindow()?.HideNotifyIcon();
                    }
                };
            }
        }

        /// <summary>
        ///     Shuts down the application.
        /// </summary>
        public ICommand ExitApplicationCommand
        {
            get
            {
                return new DelegateCommand
                {
                    CommandAction = () =>
                    {
                        // Ensure the main window allows shutdown (it normally hides to tray).
                        GetMainWindow()?.RequestExit();
                    }
                };
            }
        }

        public ICommand DisableApp
        {
            get
            {
                return new DelegateCommand
                {
                    CanExecuteFunc = () => Application.Current.MainWindow != null,
                    CommandAction = () =>
                    {
                        var mw = GetMainWindow();
                        if (mw != null) mw.GlobalSwitch = false;
                    }
                };
            }
        }

        public ICommand EnableApp
        {
            get
            {
                return new DelegateCommand
                {
                    CanExecuteFunc = () => Application.Current.MainWindow != null,
                    CommandAction = () =>
                    {
                        var mw = GetMainWindow();
                        if (mw != null) mw.GlobalSwitch = true;
                    }
                };
            }
        }
    }
}