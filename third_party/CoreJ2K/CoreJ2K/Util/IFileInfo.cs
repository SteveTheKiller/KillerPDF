namespace CoreJ2K.Util
{
    public interface IFileInfo
    {
        #region PROPERTIES

        string Name { get; }

        string FullName { get; }

        bool Exists { get; }

        #endregion

        #region METHODS

        bool Delete();

        #endregion
    }
}