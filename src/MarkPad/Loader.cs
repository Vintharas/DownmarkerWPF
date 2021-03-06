using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MarkPad
{
    internal class Loader
    {
        const string LibsFolder = "Libs";
        private const string UpdatesFolder = "Updater";

        static readonly Dictionary<string, Assembly> Libraries = new Dictionary<string, Assembly>();
        static readonly Dictionary<string, Assembly> ReflectionOnlyLibraries = new Dictionary<string, Assembly>();

        [STAThread]
        public static void Main()
        {
            var directoryName = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            if (directoryName != null && Directory.GetCurrentDirectory() != directoryName)
                Directory.SetCurrentDirectory(directoryName);

            AppDomain.CurrentDomain.AssemblyResolve += FindAssembly;

            PreloadUnmanagedLibraries();

            App.Start();
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr LoadLibrary(string dllToLoad);

        private static void PreloadUnmanagedLibraries()
        {
            // Preload correct library
            var bittyness = "x86";
            if (IntPtr.Size == 8)
                bittyness = "x64";

            var assemblyName = Assembly.GetExecutingAssembly().GetName();

            var libraries = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                .Where(s => s.StartsWith(String.Format("{1}.{2}.{0}.", bittyness, assemblyName.Name, LibsFolder)))
                .ToArray();

            var dirName = Path.Combine(Path.GetTempPath(), String.Format("{2}.{1}.{0}", assemblyName.Version, bittyness, assemblyName.Name));
            if (!Directory.Exists(dirName))
                Directory.CreateDirectory(dirName);

            foreach (var lib in libraries)
            {
                string dllPath = Path.Combine(dirName, String.Join(".", lib.Split('.').Skip(3)));

                if (!File.Exists(dllPath))
                {
                    using (Stream stm = Assembly.GetExecutingAssembly().GetManifestResourceStream(lib))
                    {
                        // Copy the assembly to the temporary file
                        try
                        {
                            using (Stream outFile = File.Create(dllPath))
                            {
                                stm.CopyTo(outFile);
                            }
                        }
                        catch
                        {
                            // This may happen if another process has already created and loaded the file.
                            // Since the directory includes the version number of this assembly we can
                            // assume that it's the same bits, so we just ignore the excecption here and
                            // load the DLL.
                        }
                    }
                }

                // We must explicitly load the DLL here because the temporary directory 
                // is not in the PATH.
                // Once it is loaded, the DllImport directives that use the DLL will use
                // the one that is already loaded into the process.
                var pointer = LoadLibrary(dllPath);

                if (lib.EndsWith("Hunspellx86.dll"))
                {
                    // The nhunspell assembly tries to do it's own loading, which fails
                    // so to help it along we set the internal pointer correctly.

                    LoadHunspellLibrary(pointer);
                }
            }

            var thisFolder = Directory.GetCurrentDirectory();
            var updates = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                .Where(s => s.StartsWith(String.Format("{0}.{1}", assemblyName.Name, UpdatesFolder)))
                .ToArray();

            foreach (var u in updates)
            {
                string dllPath = Path.Combine(thisFolder, String.Join(".", u.Split('.').Skip(2)));

                if (!File.Exists(dllPath))
                {
                    using (Stream stm = Assembly.GetExecutingAssembly().GetManifestResourceStream(u))
                    {
                        // Copy the assembly to the temporary file
                        try
                        {
                            using (Stream outFile = File.Create(dllPath))
                            {
                                stm.CopyTo(outFile);
                            }
                        }
                        catch
                        {

                        }
                    }
                }
            }
        }

        private static void LoadHunspellLibrary(IntPtr pointer)
        {
            // The pointer we want to set is private, so here comes the reflection voodoo.

            var hunspellAssembly = Assembly.GetAssembly(typeof(NHunspell.Hunspell));

            var marshalType = hunspellAssembly.GetType("NHunspell.MarshalHunspellDll");

            var dllHandleField = marshalType.GetField("dllHandle", BindingFlags.NonPublic | BindingFlags.Static);

            dllHandleField.SetValue(null, pointer);

            var getDelegateMethod = marshalType.GetMethod("GetDelegate", BindingFlags.NonPublic | BindingFlags.Static);

            SetHunspellDelegate(marshalType, getDelegateMethod, "HunspellInit", "HunspellInitDelegate");
            SetHunspellDelegate(marshalType, getDelegateMethod, "HunspellFree", "HunspellFreeDelegate");
            SetHunspellDelegate(marshalType, getDelegateMethod, "HunspellAdd", "HunspellAddDelegate");
            SetHunspellDelegate(marshalType, getDelegateMethod, "HunspellAddWithAffix", "HunspellAddWithAffixDelegate");
            SetHunspellDelegate(marshalType, getDelegateMethod, "HunspellSpell", "HunspellSpellDelegate");
            SetHunspellDelegate(marshalType, getDelegateMethod, "HunspellSuggest", "HunspellSuggestDelegate");
            SetHunspellDelegate(marshalType, getDelegateMethod, "HunspellAnalyze", "HunspellAnalyzeDelegate");
            SetHunspellDelegate(marshalType, getDelegateMethod, "HunspellStem", "HunspellStemDelegate");
            SetHunspellDelegate(marshalType, getDelegateMethod, "HunspellGenerate", "HunspellGenerateDelegate");
            SetHunspellDelegate(marshalType, getDelegateMethod, "HyphenInit", "HyphenInitDelegate");
            SetHunspellDelegate(marshalType, getDelegateMethod, "HyphenFree", "HyphenFreeDelegate");
            SetHunspellDelegate(marshalType, getDelegateMethod, "HyphenHyphenate", "HyphenHyphenateDelegate");
        }

        private static void SetHunspellDelegate(Type marshalType, MethodInfo getDelegateMethod, string call, string delegateName)
        {
            var delegateField = marshalType.GetField(call, BindingFlags.NonPublic | BindingFlags.Static);

            var delegateType = marshalType.GetNestedType(delegateName, BindingFlags.NonPublic);

            delegateField.SetValue(null, getDelegateMethod.Invoke(null, new object[] { call, delegateType }));
        }

        internal static Assembly LoadAssembly(string fullName)
        {
            Assembly a;

            var executingAssembly = Assembly.GetExecutingAssembly();

            var assemblyName = executingAssembly.GetName();

            var shortName = new AssemblyName(fullName).Name;
            if (Libraries.ContainsKey(shortName))
                return Libraries[shortName];

            var resourceName = String.Format("{0}.{2}.{1}.dll", assemblyName.Name, shortName, LibsFolder);
            var actualName = executingAssembly.GetManifestResourceNames().FirstOrDefault(n => string.Equals(n, resourceName, StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(actualName))
            {
                // The library might be a mixed mode assembly. Try loading from the bitty folders.
                var bittyness = "x86";
                if (IntPtr.Size == 8)
                    bittyness = "x64";

                resourceName = String.Format("{0}.{3}.{1}.{2}.dll", assemblyName.Name, bittyness, shortName, LibsFolder);
                actualName = executingAssembly.GetManifestResourceNames().FirstOrDefault(n => string.Equals(n, resourceName, StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrEmpty(actualName))
                {
                    Libraries[shortName] = null;
                    return null;
                }

                // Ok, mixed mode assemblies cannot be loaded through Assembly.Load.
                // See http://stackoverflow.com/questions/2945080/ and http://connect.microsoft.com/VisualStudio/feedback/details/97801/
                // But, since it's an unmanaged library we've already dumped it to disk to preload it into the process.
                // So, we'll just load it from there.
                var dirName = Path.Combine(Path.GetTempPath(), String.Format("{2}.{1}.{0}", assemblyName.Version, bittyness, assemblyName.Name));
                var dllPath = Path.Combine(dirName, String.Join(".", actualName.Split('.').Skip(3)));

                if (!File.Exists(dllPath))
                {
                    Libraries[shortName] = null;
                    return null;
                }

                a = Assembly.LoadFile(dllPath);
                Libraries[shortName] = a;
                return a;
            }

            using (var s = executingAssembly.GetManifestResourceStream(actualName))
            {
                var data = new BinaryReader(s).ReadBytes((int)s.Length);

                byte[] debugData = null;
                if (executingAssembly.GetManifestResourceNames().Contains(String.Format("{0}.{2}.{1}.pdb", assemblyName.Name, shortName, LibsFolder)))
                {
                    using (var ds = executingAssembly.GetManifestResourceStream(String.Format("{0}.{2}.{1}.pdb", assemblyName.Name, shortName, LibsFolder)))
                    {
                        debugData = new BinaryReader(ds).ReadBytes((int)ds.Length);
                    }
                }

                if (debugData != null)
                {
                    a = Assembly.Load(data, debugData);
                    Libraries[shortName] = a;
                    return a;
                }
                a = Assembly.Load(data);
                Libraries[shortName] = a;
                return a;
            }
        }

        internal static Assembly ReflectionOnlyLoadAssembly(string fullName)
        {
            var executingAssembly = Assembly.GetExecutingAssembly();

            var assemblyName = Assembly.GetExecutingAssembly().GetName();

            string shortName = new AssemblyName(fullName).Name;
            if (ReflectionOnlyLibraries.ContainsKey(shortName))
                return ReflectionOnlyLibraries[shortName];

            var resourceName = String.Format("{0}.{2}.{1}.dll", assemblyName.Name, shortName, LibsFolder);

            if (!executingAssembly.GetManifestResourceNames().Contains(resourceName))
            {
                ReflectionOnlyLibraries[shortName] = null;
                return null;
            }

            using (var s = executingAssembly.GetManifestResourceStream(resourceName))
            {
                var data = new BinaryReader(s).ReadBytes((int)s.Length);

                var a = Assembly.ReflectionOnlyLoad(data);
                ReflectionOnlyLibraries[shortName] = a;

                return a;
            }
        }

        internal static Assembly FindAssembly(object sender, ResolveEventArgs args)
        {
            return LoadAssembly(args.Name);
        }

        internal static Assembly FindReflectionOnlyAssembly(object sender, ResolveEventArgs args)
        {
            return ReflectionOnlyLoadAssembly(args.Name);
        }
    }
}
