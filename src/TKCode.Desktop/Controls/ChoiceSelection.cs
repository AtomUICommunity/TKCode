using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Controls;

/// <summary>
/// 把 <see cref="SettingsChoiceViewModel"/> 列表接到 Arc 的分段选择等 SelectingItemsControl：选中项跟随各项的 IsSelected，
/// 用户改选时执行 <see cref="CommandProperty"/>（参数是被选中的项）。视图模型不必为 Arc 控件再提供 SelectedValue。
/// </summary>
public static class ChoiceSelection
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<SelectingItemsControl, ICommand?>("Command", typeof(ChoiceSelection));

    private static readonly AttachedProperty<Binder?> BinderProperty =
        AvaloniaProperty.RegisterAttached<SelectingItemsControl, Binder?>("Binder", typeof(ChoiceSelection));

    static ChoiceSelection()
    {
        CommandProperty.Changed.AddClassHandler<SelectingItemsControl>((control, e) =>
        {
            if (e.NewValue is not null && control.GetValue(BinderProperty) is null)
            {
                control.SetValue(BinderProperty, new Binder(control));
            }
        });
    }

    public static ICommand? GetCommand(SelectingItemsControl control) => control.GetValue(CommandProperty);
    public static void SetCommand(SelectingItemsControl control, ICommand? value) => control.SetValue(CommandProperty, value);

    /// <summary>控件在可视树上时才监听数据源，离开后取消订阅（视图模型比视图活得久）。</summary>
    private sealed class Binder
    {
        private readonly SelectingItemsControl _control;
        private readonly List<INotifyPropertyChanged> _watched = [];
        private INotifyCollectionChanged? _collection;
        private bool _syncing;
        private bool _queued;

        public Binder(SelectingItemsControl control)
        {
            _control = control;
            control.SelectionChanged += OnSelectionChanged;
            control.AttachedToVisualTree += (_, _) => Attach();
            control.DetachedFromVisualTree += (_, _) => Detach();
            control.PropertyChanged += (_, e) =>
            {
                if (e.Property == ItemsControl.ItemsSourceProperty && control.IsAttachedToVisualTree())
                {
                    Detach();
                    Attach();
                }
            };
            if (control.IsAttachedToVisualTree())
            {
                Attach();
            }
        }

        private void Attach()
        {
            _collection = _control.ItemsSource as INotifyCollectionChanged;
            if (_collection is not null)
            {
                _collection.CollectionChanged += OnCollectionChanged;
            }
            Watch();
            Sync();
        }

        private void Detach()
        {
            if (_collection is not null)
            {
                _collection.CollectionChanged -= OnCollectionChanged;
                _collection = null;
            }
            Unwatch();
        }

        private void Watch()
        {
            Unwatch();
            if (_control.ItemsSource is not IEnumerable items)
            {
                return;
            }
            foreach (var item in items)
            {
                if (item is SettingsChoiceViewModel choice)
                {
                    choice.PropertyChanged += OnItemChanged;
                    _watched.Add(choice);
                }
            }
        }

        private void Unwatch()
        {
            foreach (var item in _watched)
            {
                item.PropertyChanged -= OnItemChanged;
            }
            _watched.Clear();
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Watch();
            QueueSync();
        }

        private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SettingsChoiceViewModel.IsSelected))
            {
                QueueSync();
            }
        }

        /// <summary>
        /// 视图模型换选中项时先清掉旧的、再选中新的（重建列表时先 Clear 再逐项 Add）：中间那一刻没有选中项。
        /// 合并到同一轮里、在渲染前同步一次，Arc 的选中条就从旧位置滑到新位置，而不是先消失再直接出现。
        /// </summary>
        private void QueueSync()
        {
            if (_queued)
            {
                return;
            }
            _queued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _queued = false;
                Sync();
            });
        }

        /// <summary>选中项 = IsSelected 为真的那一项（没有则不选）。</summary>
        private void Sync()
        {
            SettingsChoiceViewModel? selected = null;
            if (_control.ItemsSource is IEnumerable items)
            {
                foreach (var item in items)
                {
                    if (item is SettingsChoiceViewModel { IsSelected: true } choice)
                    {
                        selected = choice;
                        break;
                    }
                }
            }
            if (ReferenceEquals(_control.SelectedItem, selected))
            {
                return;
            }
            _syncing = true;
            try
            {
                _control.SelectedItem = selected;
            }
            finally
            {
                _syncing = false;
            }
        }

        private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _control.SelectedItem is not SettingsChoiceViewModel { IsSelected: false } choice)
            {
                return;
            }
            var command = GetCommand(_control);
            if (command?.CanExecute(choice) == true)
            {
                command.Execute(choice);
            }
            // 命令没有改变选中状态（被拒绝或异步完成）时，让控件回到视图模型的状态
            Dispatcher.UIThread.Post(Sync, DispatcherPriority.Background);
        }
    }
}
