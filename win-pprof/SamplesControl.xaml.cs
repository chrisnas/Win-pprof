using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace win_pprof
{
    public class FrameInfo
    {
        public ulong Address { get; set; }
        public string Text { get; set; }
        public string IsInlined { get; set; }
    }

    public partial class SamplesControl : UserControl
    {
        private List<int> _selectedValuesIndexes;

        public SamplesControl()
        {
            InitializeComponent();

            // replace
            //    ItemsSource = "{Binding ElementName=lvValues, Path=SelectedItem.Locations}"
            // in <ListView Name="lvFrames">
            lvValues.SelectionChanged += OnSelectedValueChanged;

            // add copy to clipboard support for frames
            lvFrames.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnFramesCopy, CanCopyIfSelectedItems));
            lvFrames.CommandBindings.Add(new CommandBinding(ApplicationCommands.Cut, OnFramesCopyAll, CanCopyIfSelectedItems));

            // add copy to clipboard support for labels ("name = value" per label)
            lvLabels.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnLabelsCopy, CanCopyIfSelectedItems));
        }

        private void OnSelectedValueChanged(object sender, SelectionChangedEventArgs e)
        {
            lvFrames.Items.Clear();

            if (e.AddedItems.Count == 0)
                return;

            var selectedItem = e.AddedItems[0];
            var sample = selectedItem as Sample;
            if (sample == null)
                return;

            foreach (var location in sample.Locations)
            {
                foreach (var frame in location.Frames)
                {
                    var frameInfo = new FrameInfo()
                    {
                        Address = location.Address,
                        IsInlined = (frame.IsInlined) ? "-" : string.Empty,
                        Text = frame.Name
                    };

                    lvFrames.Items.Add(frameInfo);
                }
            }
        }

        private void CopyFrames(ExecutedRoutedEventArgs e, bool copyAllFields)
        {
            var listview = e.OriginalSource as ListView;
            if (listview == null)
                return;

            var builder = new StringBuilder(1024);
            foreach (var item in listview.SelectedItems)
            {
                var frame = item as FrameInfo;
                if (frame == null)
                    return;

                if (copyAllFields)
                    builder.AppendLine($"{frame.Address}\t{frame.IsInlined}\t{frame.Text}");
                else
                    builder.AppendLine(frame.Text);
            }
            Clipboard.SetText(builder.ToString());
        }

        private void OnFramesCopyAll(object sender, ExecutedRoutedEventArgs e)
        {
            CopyFrames(e, true);
        }

        void OnFramesCopy(object target, ExecutedRoutedEventArgs e)
        {
            CopyFrames(e, false);
        }

        void OnLabelsCopy(object target, ExecutedRoutedEventArgs e)
        {
            var listview = e.OriginalSource as ListView;
            if (listview == null)
                return;

            var builder = new StringBuilder(1024);
            foreach (var item in listview.SelectedItems)
            {
                var label = item as Label;
                if (label == null)
                    return;

                builder.AppendLine($"{label.Key} = {label.Value}");
            }
            Clipboard.SetText(builder.ToString());
        }

        void CanCopyIfSelectedItems(object sender, CanExecuteRoutedEventArgs e)
        {
            var listview = e.OriginalSource as ListView;
            if (listview == null)
                return;

            e.CanExecute = (listview.SelectedItems.Count > 0);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (lvValues.Items.Count > 0)
            {
                lvValues.SelectedIndex = 0;
            }
        }

        public void Update(PProfFile profile)
        {
            DataContext = profile;
            _selectedValuesIndexes = null;
            lvValues.ItemsSource = null;
            lvValues.Items.Clear();

            SetValues(profile);
        }

        private void SetValues(PProfFile profile)
        {
            // each value type is a column
            var gridView = new GridView();
            lvValues.View = gridView;

            var valueTypes = profile.ValueTypes;
            var valuesCount = valueTypes.Count();
            var currentColumn = 0;
            var converter = new ZeroToEmptyConverter();
            foreach (var valueType in valueTypes)
            {
                var binding = new Binding($"Values[{currentColumn}]");
                binding.Converter = converter;
                var header = new GridViewColumnHeader();
                header.MouseDown += OnValuesMouseDown;
                header.Content = valueType.Name;
                gridView.Columns.Add(
                    new GridViewColumn
                    {
                        Header = header,
                        DisplayMemberBinding = binding,
                        Width = 90
                    }
                );

                currentColumn++;
            }
            // TODO: set the unit as tooltip because header could become too large with it

            // fill up the list with ALL samples and compute the value sum for each column
            // also count unique call stacks
            Dictionary<int, int> uniqueStacks = new Dictionary<int, int>();
            List<long> valuesSum = new List<long>(valuesCount);
            for (int i = 0; i < valuesCount; i++)
            {
                valuesSum.Add(0);
            }

            var sampleIndex = 0;
            foreach (var sample in profile.Samples)
            {
                lvValues.Items.Add(sample);

                for (int i = 0; i < valuesCount; i++)
                {
                    valuesSum[i] += sample.Values[i];
                }

                var stackKey = sample.Locations.GetHashCodes();
                int count = 0;
                uniqueStacks.TryGetValue(stackKey, out count);
                uniqueStacks[stackKey] = count + 1;

                sampleIndex++;
            }

            // show the number of unique call stacks
            tbStacksCount.Text = $"({uniqueStacks.Count})";

            // show the sum all values per column in the header tooltip
            for (int i = 0; i < valuesCount; i++)
            {
                var header = (GridViewColumnHeader)((GridViewColumn)gridView.Columns[i]).Header;
                header.ToolTip = valuesSum[i];
            }
        }

        private void ApplyFilters()
        {
            // take into account the 3 filters (selected value types, label text, frame text)
            lvValues.ItemsSource = null;
            lvValues.Items.Clear();
            var profile = DataContext as PProfFile;
            var labelFilterText = tbFilterLabels.Text;
            var frameFilterText = tbFilterFrames.Text;

            // no filter case
            if (
                string.IsNullOrEmpty(labelFilterText) &&
                string.IsNullOrEmpty(frameFilterText) &&
                (_selectedValuesIndexes == null)
                )
            {
                lvValues.ItemsSource = profile.Samples;
                lvValues.SelectedIndex = 0;
                return;
            }

            bool hasLabelFilter = !string.IsNullOrEmpty(labelFilterText);
            bool hasFrameFilter = !string.IsNullOrEmpty(frameFilterText);
            foreach (var sample in profile.Samples)
            {
                bool keepSample = true;
                if (hasLabelFilter)
                {
                    keepSample = sample.Labels.Any(label => label.Value.Contains(labelFilterText));
                }
                if (hasFrameFilter)
                {
                    keepSample &= IsInFrames(frameFilterText, sample.Locations);
                }
                if (_selectedValuesIndexes != null)
                {
                    keepSample &= HasValues(sample, _selectedValuesIndexes);
                }

                if (keepSample)
                {
                    lvValues.Items.Add(sample);
                }
            }

            if (lvValues.Items.Count > 0)
            {
                lvValues.SelectedIndex = 0;
            }
        }

        private bool HasValues(Sample sample, List<int> selectedValuesIndexes)
        {
            var index = 0;
            bool hasValues = false;

            // at least one of the selected values MUST be != 0
            foreach (var value in sample.Values)
            {
                if (selectedValuesIndexes.Contains(index))
                {
                    hasValues  |= (value != 0);
                }

                index++;
            }

            return hasValues;
        }

        private bool IsInFrames(string frameFilterText, IEnumerable<Location> locations)
        {
            foreach (var location in locations)
            {
                if (location.Frames.Any(frame => frame.Name.Contains(frameFilterText)))
                {
                    return true;
                }
            }

            return false;
        }

        private void OnValuesMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.RightButton == MouseButtonState.Pressed)
            {
                //GridViewColumnHeader gvch = sender as GridViewColumnHeader;
                //MessageBox.Show("Header Text : " + gvch.Content);

                var profile = DataContext as PProfFile;
                ValueTypePicker picker = new ValueTypePicker(profile.ValueTypes, _selectedValuesIndexes);
                if (picker.ShowDialog() != true)
                {
                    return;
                }

                // keep only samples for which the selected values are not 0
                if (picker.SelectedItems.Count == profile.ValueTypes.Count)  // all columns were selected
                {
                    _selectedValuesIndexes = null;
                }
                else
                {
                    _selectedValuesIndexes = new List<int>(picker.SelectedItems.Select(item => item.index));
                }

                ApplyFilters();
            }
        }

        private void OnFilterLabelTextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void OnFilterFrameTextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilters();
        }
    }

    // from https://stackoverflow.com/questions/8094867/good-gethashcode-override-for-list-of-foo-objects-respecting-the-order/
    internal static class Extensions
    {
        internal static int GetHashCodes<T>(this IEnumerable<T> sequence)
        {
            unchecked
            {
                int hash = 19;
                foreach (T item in sequence)
                {
                    hash = hash * 31 + (item != null ? item.GetHashCode() : 1);
                }

                return hash;
            }
        }
    }
}
