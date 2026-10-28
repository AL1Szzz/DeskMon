using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeskMon
{
    public partial class MonitorCell : UserControl
    {
        public MonitorCell()
        {
            InitializeComponent();
        }

        public string Label
        {
            set => LabelText.Text = value;
        }

        public string Value
        {
            set => ValueText.Text = value;
        }

        // Keeps a cell from breathing every second as digit counts change (9% -> 10%).
        public double ValueMinWidth
        {
            set => ValueText.MinWidth = value;
        }

        public Geometry Icon
        {
            set => IconPath.Data = value;
        }

        public static Geometry Parse(string pathData)
            => Geometry.Parse(pathData);
    }
}
