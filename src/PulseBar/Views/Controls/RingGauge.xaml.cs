using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using PulseBar.Utilities;

namespace PulseBar.Views.Controls;

/// <summary>
/// 0–100 মানের বৃত্তাকার gauge। মান অনুযায়ী রং বদলায় (accent / amber / লাল),
/// null হলে ধূসর ring আর "N/A"।
/// </summary>
public partial class RingGauge : UserControl
{

    private static readonly Duration AnimationDuration = new(TimeSpan.FromMilliseconds(250));
    private static readonly IEasingFunction Easing = CreateEasing();

    private UsageLevel _level = UsageLevel.Unknown;
    private bool _hasShownValue;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double?), typeof(RingGauge),
        new PropertyMetadata(null, (d, _) => ((RingGauge)d).OnValueChanged()));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(RingGauge),
        new PropertyMetadata(string.Empty, (d, e) => ((RingGauge)d).LabelText.Text = (string?)e.NewValue));

    public static readonly DependencyProperty SubTextProperty = DependencyProperty.Register(
        nameof(SubText), typeof(string), typeof(RingGauge),
        new PropertyMetadata(string.Empty, (d, e) => ((RingGauge)d).SubTextBlock.Text = (string?)e.NewValue));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(RingGauge),
        new PropertyMetadata("%", (d, _) => ((RingGauge)d).OnValueChanged()));

    public static readonly DependencyProperty WarningThresholdProperty = DependencyProperty.Register(
        nameof(WarningThreshold), typeof(double), typeof(RingGauge),
        new PropertyMetadata(UsageLevels.UsageWarning, (d, _) => ((RingGauge)d).OnValueChanged()));

    public static readonly DependencyProperty CriticalThresholdProperty = DependencyProperty.Register(
        nameof(CriticalThreshold), typeof(double), typeof(RingGauge),
        new PropertyMetadata(UsageLevels.UsageCritical, (d, _) => ((RingGauge)d).OnValueChanged()));

    /// <summary>Ring এর ব্যাস (DIP); stroke আর লেখার মাপ এর সাথে মিলিয়ে বদলায়।</summary>
    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(
        nameof(Diameter), typeof(double), typeof(RingGauge),
        new PropertyMetadata(104.0, (d, _) => ((RingGauge)d).ApplyDiameter()));

    /// <summary>Ring এ এখন যতটা আঁকা আছে (animation এর জন্য)।</summary>
    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(RingGauge),
        new PropertyMetadata(0.0, (d, e) => ((RingGauge)d).DrawArc((double)e.NewValue)));

    public RingGauge()
    {
        InitializeComponent();
        ApplyDiameter();
        OnValueChanged();
    }

    public double Diameter
    {
        get => (double)GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    private double Thickness => Math.Max(4, Math.Round(Diameter * 0.077));

    private void ApplyDiameter()
    {
        double d = Diameter;
        RingHost.Width = d;
        RingHost.Height = d;
        Track.StrokeThickness = Thickness;
        Arc.StrokeThickness = Thickness;
        ValueText.FontSize = Math.Round(d * 0.21);
        LabelText.FontSize = Math.Max(10, Math.Round(d * 0.12));
        DrawArc((double)GetValue(DisplayValueProperty));
    }

    public double? Value
    {
        get => (double?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string SubText
    {
        get => (string)GetValue(SubTextProperty);
        set => SetValue(SubTextProperty, value);
    }

    /// <summary>সংখ্যার পরের একক, যেমন "%" বা "°C"।</summary>
    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public double WarningThreshold
    {
        get => (double)GetValue(WarningThresholdProperty);
        set => SetValue(WarningThresholdProperty, value);
    }

    public double CriticalThreshold
    {
        get => (double)GetValue(CriticalThresholdProperty);
        set => SetValue(CriticalThresholdProperty, value);
    }

    private void OnValueChanged()
    {
        double? value = Value;

        if (value is not { } v || double.IsNaN(v))
        {
            ValueText.Text = "N/A";
            ValueText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorDisabledBrush");
            SetLevel(UsageLevel.Unknown);
            SetDisplayValue(0, animate: false);
            _hasShownValue = false;
            return;
        }

        ValueText.Text = Math.Round(v).ToString("0", CultureInfo.CurrentCulture) + Unit;
        ValueText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        SetLevel(UsageLevels.Evaluate(v, WarningThreshold, CriticalThreshold));

        // প্রথমবার মান এলে (বা N/A থেকে ফিরলে) সরাসরি বসে, পরের বদলগুলো মসৃণভাবে
        SetDisplayValue(Math.Clamp(v, 0, 100), animate: _hasShownValue && SystemParameters.ClientAreaAnimation);
        _hasShownValue = true;
    }

    private void SetLevel(UsageLevel level)
    {
        if (level == _level)
        {
            return;
        }

        _level = level;
        switch (level)
        {
            case UsageLevel.Warning:
                Arc.Stroke = UsageLevels.AmberBrush;
                break;
            case UsageLevel.Critical:
                Arc.Stroke = UsageLevels.RedBrush;
                break;
            default:
                Arc.SetResourceReference(Shape.StrokeProperty, "AccentFillColorDefaultBrush");
                break;
        }
    }

    private void SetDisplayValue(double target, bool animate)
    {
        if (!animate)
        {
            BeginAnimation(DisplayValueProperty, null);
            SetValue(DisplayValueProperty, target);
            DrawArc(target);
            return;
        }

        var animation = new DoubleAnimation(target, AnimationDuration) { EasingFunction = Easing };
        animation.Freeze();
        BeginAnimation(DisplayValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void DrawArc(double value)
    {
        double fraction = value / 100.0;
        if (fraction <= 0.001)
        {
            Arc.Data = null;
            return;
        }

        double center = Diameter / 2;
        double radius = center - Thickness / 2;

        Geometry geometry;
        if (fraction >= 0.9999)
        {
            geometry = new EllipseGeometry(new Point(center, center), radius, radius);
        }
        else
        {
            double angle = fraction * 2 * Math.PI;
            var start = new Point(center, center - radius);
            var end = new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle));

            var stream = new StreamGeometry();
            using (var ctx = stream.Open())
            {
                ctx.BeginFigure(start, isFilled: false, isClosed: false);
                ctx.ArcTo(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
            }

            geometry = stream;
        }

        geometry.Freeze();
        Arc.Data = geometry;
    }

    private static IEasingFunction CreateEasing()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        easing.Freeze();
        return easing;
    }
}
