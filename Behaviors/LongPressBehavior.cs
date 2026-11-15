using System;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;

namespace BookViewer.Behaviors
{
    /// <summary>
    /// Cross-platform long-press behavior. Fires LongPressed after PressDuration ms
    /// of uninterrupted press. Suppresses the subsequent tap by setting Handled.
    /// </summary>
    public class LongPressBehavior : Behavior<View>
    {
        public static readonly BindableProperty CommandProperty =
            BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(LongPressBehavior));

        public static readonly BindableProperty CommandParameterProperty =
            BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(LongPressBehavior));

        public static readonly BindableProperty PressDurationProperty =
            BindableProperty.Create(nameof(PressDuration), typeof(int), typeof(LongPressBehavior), 650);

        public ICommand Command
        {
            get => (ICommand)GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        public object CommandParameter
        {
            get => GetValue(CommandParameterProperty);
            set => SetValue(CommandParameterProperty, value);
        }

        public int PressDuration
        {
            get => (int)GetValue(PressDurationProperty);
            set => SetValue(PressDurationProperty, value);
        }

        public event EventHandler LongPressed;

        private View _view;
        private IDispatcherTimer _timer;
        private bool _isPressed;
        private bool _alreadyFired;

        protected override void OnAttachedTo(View bindable)
        {
            base.OnAttachedTo(bindable);
            _view = bindable;

            var pointer = new PointerGestureRecognizer();
            pointer.PointerPressed += OnPointerPressed;
            pointer.PointerReleased += OnPointerReleased;
            pointer.PointerExited += OnPointerReleased;
            _view.GestureRecognizers.Add(pointer);

            _view.HandlerChanged += OnHandlerChanged;
        }

        protected override void OnDetachingFrom(View bindable)
        {
            _view.HandlerChanged -= OnHandlerChanged;
            StopTimer();
            base.OnDetachingFrom(bindable);
            _view = null;
        }

        private void OnHandlerChanged(object sender, EventArgs e) => HookNativeGestures();

        private void HookNativeGestures()
        {
#if IOS || MACCATALYST
            if (_view?.Handler?.PlatformView is UIKit.UIView uiView)
            {
                uiView.UserInteractionEnabled = true;
                var lp = new UIKit.UILongPressGestureRecognizer(OnNativeLongPress);
                lp.MinimumPressDuration = PressDuration / 1000.0;
                lp.CancelsTouchesInView = false;
                uiView.AddGestureRecognizer(lp);
            }
#elif ANDROID
            if (_view?.Handler?.PlatformView is Android.Views.View androidView)
            {
                androidView.LongClickable = true;
                androidView.LongClick += (s, e) =>
                {
                    Fire();
                    e.Handled = true;
                };
            }
#endif
        }

#if IOS || MACCATALYST
        private void OnNativeLongPress(UIKit.UILongPressGestureRecognizer recognizer)
        {
            if (recognizer.State == UIKit.UIGestureRecognizerState.Began)
                Fire();
        }
#endif

        private void OnPointerPressed(object sender, PointerEventArgs e)
        {
            _isPressed = true;
            _alreadyFired = false;
            StartTimer();
        }

        private void OnPointerReleased(object sender, PointerEventArgs e)
        {
            _isPressed = false;
            StopTimer();
        }

        private void StartTimer()
        {
            if (_view == null) return;
            _timer?.Stop();
            _timer = _view.Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(PressDuration);
            _timer.IsRepeating = false;
            _timer.Tick += (s, e) =>
            {
                if (!_isPressed || _alreadyFired) return;
                Fire();
                _timer?.Stop();
            };
            _timer.Start();
        }

        private void StopTimer()
        {
            try { _timer?.Stop(); } catch { }
            _timer = null;
        }

        private void Fire()
        {
            if (_alreadyFired) return;
            _alreadyFired = true;

            try { LongPressed?.Invoke(this, EventArgs.Empty); } catch { }

            var cmd = Command;
            if (cmd != null && cmd.CanExecute(CommandParameter))
            {
                try { cmd.Execute(CommandParameter); } catch { }
            }
        }
    }
}
