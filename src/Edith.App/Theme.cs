using System.Windows;
using System.Windows.Markup;

namespace Edith.App;

/// <summary>Shared look of EDITH's windows: a thin dark scroll bar instead of the grey system one.</summary>
public static class Theme
{
    private const string ScrollBarXaml = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style x:Key="EdithThumb" TargetType="Thumb">
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Thumb">
                  <Border x:Name="bar" CornerRadius="3" Background="#59A9C6E0" Margin="1,0"/>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bar" Property="Background" Value="#99A9C6E0"/></Trigger>
                    <Trigger Property="IsDragging" Value="True"><Setter TargetName="bar" Property="Background" Value="#CC55DFFF"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="ScrollBar">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Width" Value="8"/>
            <Setter Property="MinWidth" Value="8"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ScrollBar">
                  <Grid Background="Transparent">
                    <Track x:Name="PART_Track" IsDirectionReversed="True">
                      <Track.Thumb><Thumb Style="{StaticResource EdithThumb}"/></Track.Thumb>
                    </Track>
                  </Grid>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
            <Style.Triggers>
              <Trigger Property="Orientation" Value="Horizontal">
                <Setter Property="Width" Value="Auto"/>
                <Setter Property="MinWidth" Value="0"/>
                <Setter Property="Height" Value="8"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="ScrollBar">
                      <Grid Background="Transparent">
                        <Track x:Name="PART_Track">
                          <Track.Thumb><Thumb Style="{StaticResource EdithThumb}"/></Track.Thumb>
                        </Track>
                      </Grid>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Trigger>
            </Style.Triggers>
          </Style>
        </ResourceDictionary>
        """;

    private static ResourceDictionary? _dictionary;

    /// <summary>Adds the shared styles to a window's resources.</summary>
    public static void Apply(FrameworkElement window)
    {
        _dictionary ??= (ResourceDictionary)XamlReader.Parse(ScrollBarXaml);
        window.Resources.MergedDictionaries.Add(_dictionary);
    }
}
