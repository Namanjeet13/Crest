using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace Crest.Controls;

/// <summary>Liquid glass: see Shaders\Glass.hlsl. The element it sits on is the sharp screen picture.</summary>
public sealed class GlassEffect : ShaderEffect
{
    private static readonly PixelShader Shader = new()
    {
        UriSource = new Uri("pack://application:,,,/Crest;component/Shaders/Glass.ps"),
    };

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(GlassEffect), 0, SamplingMode.Bilinear);
    public static readonly DependencyProperty FrostProperty = RegisterPixelShaderSamplerProperty("Frost", typeof(GlassEffect), 1, SamplingMode.Bilinear);
    public static readonly DependencyProperty SizeLensProperty = Reg("SizeLens", 0);
    public static readonly DependencyProperty BoxProperty = Reg("Box", 1);
    public static readonly DependencyProperty LookProperty = Reg("Look", 2);
    public static readonly DependencyProperty MiscProperty = Reg("Misc", 3);

    private static DependencyProperty Reg(string name, int register) =>
        DependencyProperty.Register(name, typeof(Point4D), typeof(GlassEffect),
            new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(register)));

    public GlassEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(FrostProperty);
        UpdateShaderValue(SizeLensProperty);
        UpdateShaderValue(BoxProperty);
        UpdateShaderValue(LookProperty);
        UpdateShaderValue(MiscProperty);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Brush Frost { get => (Brush)GetValue(FrostProperty); set => SetValue(FrostProperty, value); }
    public Point4D SizeLens { get => (Point4D)GetValue(SizeLensProperty); set => SetValue(SizeLensProperty, value); }
    public Point4D Box { get => (Point4D)GetValue(BoxProperty); set => SetValue(BoxProperty, value); }
    public Point4D Look { get => (Point4D)GetValue(LookProperty); set => SetValue(LookProperty, value); }
    public Point4D Misc { get => (Point4D)GetValue(MiscProperty); set => SetValue(MiscProperty, value); }
}
