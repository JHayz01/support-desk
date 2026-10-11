using System.Windows.Media;
namespace SupportDesk.Desktop;
public static class ThemePalette
{
    public static readonly string[] Names = ["Темная", "Светлая", "Полночь"];
    static readonly string[] Colors = ["B9AEF5", "151720", "1D2130", "212535", "222637", "252A3B", "2A2E40", "2B3044", "30364C", "34304B", "3A4057", "3D435A", "41465E", "4A526D", "51466E", "63549B", "707890", "959DB4", "9DA3B9", "A397EC", "A99CDA", "ACB2C7", "ADB3CA", "CCD0E2", "E8EAF3", "E9EAF0", "EAEBF3", "EBECF5", "F3F4FA"];
    static readonly Dictionary<string,string> Light = new() {["B9AEF5"]="66528D",["151720"]="F4F5FA",["E9EAF0"]="20283A",["9DA3B9"]="566179",["2A2E40"]="E3E7F1",["EBECF5"]="20283A",["41465E"]="D2DBED",["222637"]="FFFFFF",["EAEBF3"]="20283A",["3D435A"]="B8C2D5",["E8EAF3"]="20283A",["51466E"]="D9D1EF",["252A3B"]="FFFFFF",["3A4057"]="BBC5D7",["ADB3CA"]="5F6A82",["4A526D"]="AAB6CD",["CCD0E2"]="35415A",["212535"]="FFFFFF",["30364C"]="CAD2E2",["2B3044"]="EDF1FA",["34304B"]="E4DDF6",["A397EC"]="8071B5",["F3F4FA"]="303B56",["63549B"]="63549B",["ACB2C7"]="5E6980",["A99CDA"]="66528D",["707890"]="748098",["959DB4"]="5E6980",["1D2130"]="EBEEF6"};
    public static void Apply(string theme)
    {
        foreach (string key in Colors)
        {
            string value = theme == "Светлая" ? Light.GetValueOrDefault(key, key) : theme == "Полночь" ? key switch { "151720" => "0B1423", "212535" => "142238", "1D2130" => "101D30", "222637" => "192A43", "252A3B" => "192A43", "63549B" => "286B85", _ => key } : key;
            var color = (Color)ColorConverter.ConvertFromString("#"+value);
            string name = "C"+key;
            if (Application.Current.Resources[name] is SolidColorBrush brush && !brush.IsFrozen) brush.Color = color;
            else Application.Current.Resources[name] = new SolidColorBrush(color);
        }
    }
    public static Brush Brush(string hex) => Application.Current.Resources["C"+hex.TrimStart('#')] as Brush ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
}
