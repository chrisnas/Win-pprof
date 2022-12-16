using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace win_pprof
{
    public partial class ValueTypePicker : Window
    {
        private List<(int index, ValueType valueType)> _selectedItems;  // TODO just IList<int>
        private IReadOnlyList<int> _selectedIndexes;

        public ValueTypePicker(IReadOnlyList<ValueType> valueTypes, IReadOnlyList<int> selectedIndexes)
        {
            InitializeComponent();
            _selectedIndexes = selectedIndexes;
            lbValueTypes.ItemsSource = valueTypes;

            if (_selectedIndexes == null)
            {
                lbValueTypes.SelectAll();
                return;
            }

            foreach (var index in _selectedIndexes)
            {
                lbValueTypes.SelectedItems.Add(lbValueTypes.Items[index]);
            }
        }
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _selectedItems = new List<(int index, ValueType valueType)>();
        }


        public IReadOnlyList<(int index, ValueType valueType)> SelectedItems
        {
            get { return _selectedItems; }
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            if (lbValueTypes.SelectedItems.Count == 0)
            {
                MessageBox.Show("Select at least 1 column...");
                return;
            }

            var selectedItems = lbValueTypes.SelectedItems;
            var current = 0;
            foreach (var item in lbValueTypes.Items)
            {
                if (selectedItems.Contains(item))
                {
                    (int index, ValueType valueType) tuple = (current, (ValueType)item);
                    _selectedItems.Add(tuple);
                }

                current++;
            }

            DialogResult = true;
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyboardDevice.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.U)
                {
                    lbValueTypes.UnselectAll();
                }
                else
                if (e.Key == Key.S)
                {
                    lbValueTypes.SelectAll();
                }
            }
        }
    }
}
