using System;

namespace EasyEDA_Loader
{
    public static class ImportLibrarySavePolicy
    {
        public static bool SaveLibrariesAfterImport => false;

        public static void EnsureAutomaticLibrarySaveIsDisabled()
        {
            if (SaveLibrariesAfterImport)
                throw new InvalidOperationException("Automatic library saving after import is disabled. Review and save Altium libraries manually.");
        }
    }
}
