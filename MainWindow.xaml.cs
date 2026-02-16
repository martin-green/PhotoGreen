using System.Windows;
using PhotoGreen.ViewModels;

namespace PhotoGreen
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }
    }
}