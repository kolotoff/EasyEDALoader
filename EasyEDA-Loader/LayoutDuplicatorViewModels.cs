using System.Collections.ObjectModel;
using System.ComponentModel;

namespace EasyEDA_Loader
{
    internal sealed class LayoutComponentRowViewModel : INotifyPropertyChanged
    {
        private bool isChecked = true;

        public string Designator { get; set; }
        public string Comment { get; set; }
        public string Description { get; set; }
        public string Footprint { get; set; }
        public string Layer { get; set; }
        public double XMm { get; set; }
        public double YMm { get; set; }
        public double Rotation { get; set; }
        public LayoutComponentSnapshot Component { get; set; }

        public bool IsChecked
        {
            get => isChecked;
            set
            {
                if (isChecked == value)
                    return;

                isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public static LayoutComponentRowViewModel FromComponent(LayoutComponentSnapshot component)
        {
            return new LayoutComponentRowViewModel
            {
                Component = component,
                Designator = component?.Designator,
                Comment = component?.Comment,
                Description = component?.Description,
                Footprint = component?.Footprint,
                Layer = component?.Layer,
                XMm = component?.XMm ?? 0,
                YMm = component?.YMm ?? 0,
                Rotation = component?.Rotation ?? 0,
                IsChecked = true
            };
        }
    }

    internal sealed class LayoutDuplicatorViewModel
    {
        public ObservableCollection<LayoutComponentRowViewModel> SourceComponents { get; } =
            new ObservableCollection<LayoutComponentRowViewModel>();

        public ObservableCollection<LayoutComponentRowViewModel> TargetAnchors { get; } =
            new ObservableCollection<LayoutComponentRowViewModel>();

        public ObservableCollection<OllamaModelInfo> Models { get; } =
            new ObservableCollection<OllamaModelInfo>();
    }
}
