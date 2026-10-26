using System.Web.Script.Serialization;

namespace FNHybrid
{
    public static class JsonH
    {
        static readonly JavaScriptSerializer _ser = new JavaScriptSerializer();

        public static T Parse<T>(string json)
        {
            return _ser.Deserialize<T>(json);
        }

        public static string Dump(object o)
        {
            return _ser.Serialize(o);
        }
    }
}
