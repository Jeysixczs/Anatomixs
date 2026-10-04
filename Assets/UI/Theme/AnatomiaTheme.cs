// Anatomia 3D - shared student theme switch.
//
// The minimalist theme (Theme/AnatomiaTheme.uss) replaces the old purple/green/blue
// header gradients with a flat, background-led look. The student controllers used to
// paint those gradients at runtime as inline styles, which would override USS, so each
// gradient method now returns early while UseGradientChrome is false.
//
// Set UseGradientChrome = true (e.g. from a bootstrap script) to bring the old gradients
// back; the theme USS will still apply but inline gradients will win where painted.
public static class AnatomiaTheme
{
    public static bool UseGradientChrome = false;
}
