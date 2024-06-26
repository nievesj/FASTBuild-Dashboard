﻿using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Caliburn.Micro;
using FastBuild.Dashboard.Services.Build;
using FastBuild.Dashboard.Support;

namespace FastBuild.Dashboard.Views.Build;

internal class TimeRulerView : Control
{
    private static readonly GrainStop[] GrainStops =
    {
        new(600, 300),
        new(300, 60),
        new(120, 60),
        new(60, 30),
        new(30, 10),
        new(10, 5),
        new(5, 1),
        new(2, 1),
        new(1, 0.5),
        new(0.5, 0.1),
        new(0.2, 0.1),
        new(0.1, 0.05)
    };

    public static readonly DependencyProperty MinorTickBrushProperty =
        DependencyProperty.Register("MinorTickBrush", typeof(Brush), typeof(TimeRulerView),
            new UIPropertyMetadata(Brushes.Black, AffectsVisual));

    public static readonly DependencyProperty MajorTickBrushProperty =
        DependencyProperty.Register("MajorTickBrush", typeof(Brush), typeof(TimeRulerView),
            new UIPropertyMetadata(Brushes.Black, AffectsVisual));

    public static readonly DependencyProperty TimeLabelStyleProperty =
        DependencyProperty.Register("TimeLabelStyle", typeof(Style), typeof(TimeRulerView),
            new UIPropertyMetadata(null, OnTimeLabelStyleChanged));

    private readonly TextBlock _timeLabelStyleBridge = new TextBlock();
    private Typeface _timeLabelTypeface;

    public TimeRulerView()
    {
        var buildViewportService = IoC.Get<IBuildViewportService>();
        buildViewportService.ScalingChanged += OnPreScalingChanged;
        buildViewportService.ViewTimeRangeChanged += OnViewTimeRangeChanged;
        ClipToBounds = true;

        Loaded += OnLoaded;
    }


    public Brush MinorTickBrush
    {
        get => (Brush)GetValue(MinorTickBrushProperty);
        set => SetValue(MinorTickBrushProperty, value);
    }


    public Brush MajorTickBrush
    {
        get => (Brush)GetValue(MajorTickBrushProperty);
        set => SetValue(MajorTickBrushProperty, value);
    }


    public Style TimeLabelStyle
    {
        get => (Style)GetValue(TimeLabelStyleProperty);
        set => SetValue(TimeLabelStyleProperty, value);
    }

    private static GrainStop CalculateGrain(double scaling)
    {
        foreach (var grainStop in GrainStops)
            if (scaling <= grainStop.Scaling)
                return grainStop;

        return GrainStops.Last();
    }

    private static string GetLabelTextFormat(double totalSeconds, double interval)
    {
        var formatBuilder = new StringBuilder();
        if (totalSeconds >= 3600) formatBuilder.Append(@"h\:m");

        formatBuilder.Append(@"m\:ss");

        if (interval % 1 > 0)
        {
            formatBuilder.Append(@"\.");

            for (var i = 0; i < 2; ++i)
                if (interval % Math.Pow(10, i) > 0)
                    formatBuilder.Append('f');
        }

        return formatBuilder.ToString();
    }

    private static void OnTimeLabelStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((TimeRulerView)d).OnTimeLabelStyleChanged((Style)e.NewValue);
    }

    private static void AffectsVisual(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((TimeRulerView)d).InvalidateVisual();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var window = this.FindAncestor<Window>();
        if (window != null)
        {
            _timeLabelStyleBridge.FontFamily = window.FontFamily;
            UpdateTimeLabelTypeface();
        }
    }

    private void OnTimeLabelStyleChanged(Style value)
    {
        _timeLabelStyleBridge.Style = value;
        UpdateTimeLabelTypeface();
    }

    private void UpdateTimeLabelTypeface()
    {
        _timeLabelTypeface = new Typeface(
            _timeLabelStyleBridge.FontFamily,
            _timeLabelStyleBridge.FontStyle,
            _timeLabelStyleBridge.FontWeight,
            _timeLabelStyleBridge.FontStretch);

        InvalidateVisual();
    }

    private void OnViewTimeRangeChanged(object sender, EventArgs e)
    {
        InvalidateVisual();
    }

    private void OnPreScalingChanged(object sender, EventArgs e)
    {
        InvalidateVisual();
    }


    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        dc.DrawRectangle(Background, null, LayoutInformation.GetLayoutSlot(this));

        var buildViewportService = IoC.Get<IBuildViewportService>();

        var startTime = buildViewportService.ViewStartTimeOffsetSeconds;
        var endTime = buildViewportService.ViewEndTimeOffsetSeconds;
        var duration = endTime - startTime;

        var scaling = buildViewportService.Scaling;
        var grain = CalculateGrain(scaling);

        var positiveStartTime = Math.Max(0, startTime);

        var firstMajorTickTime = positiveStartTime - positiveStartTime % grain.MajorInterval;
        var firstMinorTickTime = positiveStartTime - positiveStartTime % grain.MinorInterval;

        var lastMajorTickTime = endTime + grain.MajorInterval - endTime % grain.MajorInterval;
        var lastMinorTickTime = endTime + grain.MinorInterval - endTime % grain.MajorInterval;

        var realStartTime = Math.Min(firstMinorTickTime, firstMajorTickTime);
        var realEndTime = Math.Max(lastMajorTickTime, lastMinorTickTime);

        var labelTextFormat = GetLabelTextFormat(duration, grain.MajorInterval);

        for (var time = realStartTime; time <= realEndTime; time += grain.MajorInterval)
            DrawMajorTick(dc, (time - startTime) * scaling, time, labelTextFormat);

        for (var time = realStartTime; time >= 0 && time <= realEndTime; time += grain.MinorInterval)
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (time % grain.MajorInterval == 0) continue;

            DrawMinorTick(dc, (time - startTime) * scaling);
        }
    }

    private void DrawMinorTick(DrawingContext dc, double x)
    {
        dc.DrawRectangle(MinorTickBrush, null, new Rect(x - 0.5, ActualHeight - 8, 1, 8));
    }

    private void DrawMajorTick(DrawingContext dc, double x, double time, string labelTextFormat)
    {
        dc.DrawRectangle(MajorTickBrush, null, new Rect(x - 1, ActualHeight - 10, 2, 10));

        if (time > 0)
        {
#if DEBUG
            if (App.IsInDesignTime) return;
#endif
            var text = new FormattedText(
                TimeSpan.FromSeconds(time).ToString(labelTextFormat),
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                _timeLabelTypeface,
                _timeLabelStyleBridge.FontSize,
                MajorTickBrush, 
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            dc.DrawText(text, new Point(x - text.Width / 2, ActualHeight - 22));
        }
    }

    private struct GrainStop
    {
        private const double MinimumMajorTickDistance = 160;

        public double Scaling { get; }
        public double MajorInterval { get; }
        public double MinorInterval { get; }

        public GrainStop(double majorInterval, double minorInterval)
        {
            Scaling = MinimumMajorTickDistance / majorInterval;
            MajorInterval = majorInterval;
            MinorInterval = minorInterval;
        }
    }
}