using UnityEngine;

namespace AI003.Net
{
    /// <summary>
    /// Stable per-install client id. The server keys room membership by this id,
    /// so a reconnecting socket can resume its fighter instead of starting over.
    /// </summary>
    public static class ClientIdentity
    {
        private const string Key = "AI003.ClientId";

        private static string _id;

        public static string Id
        {
            get
            {
                if (!string.IsNullOrEmpty(_id)) return _id;

                _id = PlayerPrefs.GetString(Key, string.Empty);
                if (string.IsNullOrEmpty(_id))
                {
                    _id = System.Guid.NewGuid().ToString("N");
                    PlayerPrefs.SetString(Key, _id);
                    PlayerPrefs.Save();
                }
                return _id;
            }
        }
    }
}
