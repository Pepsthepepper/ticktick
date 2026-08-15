using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Diagnostics;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace TTPatcher
{
    // dnlib implementation of the patcher
    public class DnlibAssemblyPatcher : IAssemblyPatcher
    {
        public bool PatchAssembly(string inputPath, string outputPath)
        {
            try
            {
                Console.WriteLine("Loading assembly with dnlib...");

                // Quick sanity check: make sure the file looks like a PE (starts with "MZ")
                using (var fs = File.OpenRead(inputPath))
                {
                    int b1 = fs.ReadByte();
                    int b2 = fs.ReadByte();
                    if (b1 != 'M' || b2 != 'Z')
                    {
                        Console.WriteLine($"Invalid DOS signature: first bytes 0x{b1:X2} 0x{b2:X2}. The file is not a PE executable. Aborting patch.");
                        Console.WriteLine($"File: {inputPath}, Size: {fs.Length} bytes, Extension: {Path.GetExtension(inputPath)}");
                        return false;
                    }
                }

                // Try to load the assembly directly (most common case)
                try
                {
                    var module = ModuleDefMD.Load(inputPath);
                    Console.WriteLine($"Module loaded: {module.Name}");

                    // Find and patch the UserModel
                    var patchSuccess = PatchUserModel(module);

                    if (!patchSuccess)
                    {
                        Console.WriteLine("Failed to patch UserModel properties.");
                        return false;
                    }

                    // Save the patched assembly
                    Console.WriteLine($"Saving patched assembly to: {outputPath}");
                    module.Write(outputPath);
                    Console.WriteLine("Assembly saved successfully!");

                    return true;
                }
                catch (Exception loadEx)
                {
                    Console.WriteLine($"Direct load failed: {loadEx.Message}");
                    Console.WriteLine("Attempting to locate managed assemblies inside the input (ZIP/SFX/MSI) and patch them...");

                    // Try to extract and patch from common installer/archive formats (ZIP/SFX)
                    var patchedFromArchive = TryPatchFromArchive(inputPath, outputPath);
                    if (patchedFromArchive)
                    {
                        Console.WriteLine("Patched assembly extracted from installer and saved.");
                        return true;
                    }

                    // Fall through to outer catch for full error reporting
                    throw;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during patching: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        private bool PatchUserModel(ModuleDef module)
        {
            Console.WriteLine("Searching for UserModel type...");

            // Find the UserModel type
            var userModelType = module.Types.FirstOrDefault(t => t.FullName == "ticktick_WPF.Models.UserModel");
            if (userModelType == null)
            {
                Console.WriteLine("UserModel type not found!");
                LogAvailableTypes(module);
                return false;
            }

            Console.WriteLine($"Found UserModel: {userModelType.FullName}");
            Console.WriteLine($"Properties in UserModel: {userModelType.Properties.Count}");

            // Patch both properties
            var proPatch = PatchProProperty(userModelType);
            var proEndDatePatch = PatchProEndDateProperty(userModelType);

            return proPatch && proEndDatePatch;
        }

        private bool PatchProProperty(TypeDef userModelType)
        {
            var proProperty = userModelType.Properties.FirstOrDefault(p => p.Name == "pro");
            if (proProperty == null)
            {
                Console.WriteLine("Property 'pro' not found in UserModel.");
                return false;
            }

            // Remove setter if it exists
            if (proProperty.SetMethod != null)
            {
                userModelType.Methods.Remove(proProperty.SetMethod);
                proProperty.SetMethod = null;
                Console.WriteLine("Removed 'pro' property setter.");
            }

            // Modify getter to return true
            if (proProperty.GetMethod != null)
            {
                var getter = proProperty.GetMethod;
                getter.Body = new CilBody();

                // Create IL instructions: load true, return
                getter.Body.Instructions.Add(OpCodes.Ldc_I4_1.ToInstruction()); // Load 1 (true)
                getter.Body.Instructions.Add(OpCodes.Ret.ToInstruction());       // Return

                Console.WriteLine("Patched 'pro' property to return true.");
                return true;
            }

            Console.WriteLine("Property 'pro' has no getter method.");
            return false;
        }

        private bool PatchProEndDateProperty(TypeDef userModelType)
        {
            var proEndDateProperty = userModelType.Properties.FirstOrDefault(p => p.Name == "proEndDate");
            if (proEndDateProperty == null)
            {
                Console.WriteLine("Property 'proEndDate' not found in UserModel.");
                return false;
            }

            Console.WriteLine($"proEndDate property type: {proEndDateProperty.PropertySig.RetType}");

            // Remove setter if it exists
            if (proEndDateProperty.SetMethod != null)
            {
                userModelType.Methods.Remove(proEndDateProperty.SetMethod);
                proEndDateProperty.SetMethod = null;
                Console.WriteLine("Removed 'proEndDate' property setter.");
            }

            if (proEndDateProperty.GetMethod != null)
            {
                var getter = proEndDateProperty.GetMethod;
                var module = userModelType.Module;

                // Always resolve DateTime from corlib — scanning the assembly for an existing
                // reference is unreliable and can pick up a reference to the wrong assembly,
                // causing MissingFieldException at runtime.
                var dateTimeRef = module.CorLibTypes.GetTypeRef("System", "DateTime");
                var dateTimeSig = new ValueTypeSig(dateTimeRef);

                // DateTime.MaxValue field reference with guaranteed-correct declaring type
                var maxValueFieldRef = new MemberRefUser(module, "MaxValue",
                    new FieldSig(dateTimeSig), dateTimeRef);

                // Return type is Nullable<DateTime>, so we must call Nullable<DateTime>::.ctor.
                // Returning a bare DateTime for a DateTime? return type is a type-stack mismatch.
                var nullableRef = module.CorLibTypes.GetTypeRef("System", "Nullable`1");
                var nullableInstSig = new GenericInstSig(new ValueTypeSig(nullableRef), dateTimeSig);
                var nullableSpec = new TypeSpecUser(nullableInstSig);

                // The open-type ctor sig uses !0 (GenericVar 0) for the T parameter
                var ctorSig = MethodSig.CreateInstance(module.CorLibTypes.Void, new GenericVar(0));
                var ctorRef = new MemberRefUser(module, ".ctor", ctorSig, nullableSpec);

                getter.Body = new CilBody();
                // ldsfld DateTime::MaxValue → newobj Nullable<DateTime>::.ctor(!0) → ret
                getter.Body.Instructions.Add(OpCodes.Ldsfld.ToInstruction(maxValueFieldRef));
                getter.Body.Instructions.Add(OpCodes.Newobj.ToInstruction(ctorRef));
                getter.Body.Instructions.Add(OpCodes.Ret.ToInstruction());

                Console.WriteLine("Patched 'proEndDate' to return new DateTime?(DateTime.MaxValue).");
                return true;
            }

            Console.WriteLine("Property 'proEndDate' has no getter method.");
            return false;
        }

        private void LogAvailableTypes(ModuleDef module)
        {
            Console.WriteLine($"Total types in module: {module.Types.Count}");
            Console.WriteLine("Sample types (first 10):");

            foreach (var type in module.Types.Take(10))
            {
                Console.WriteLine($"  - {type.FullName}");
            }

            if (module.Types.Count > 10)
            {
                Console.WriteLine($"  ... and {module.Types.Count - 10} more types");
            }
        }

        private bool TryPatchFromArchive(string inputPath, string outputPath)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "TTPatcher_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // Attempt to treat the input as a ZIP or self-extracting ZIP (SFX)
                try
                {
                    using (var fs = File.OpenRead(inputPath))
                    using (var za = new ZipArchive(fs, ZipArchiveMode.Read, true))
                    {
                        Console.WriteLine($"Archive detected with {za.Entries.Count} entries.");
                        foreach (var entry in za.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name))
                                continue; // skip directories

                            if (!entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                                continue;

                            var tempFile = Path.Combine(tempDir, entry.Name);
                            Console.WriteLine($"Extracting candidate: {entry.FullName}");
                            entry.ExtractToFile(tempFile);

                            try
                            {
                                var module = ModuleDefMD.Load(tempFile);
                                Console.WriteLine($"Loaded candidate assembly: {entry.FullName}");
                                var patched = PatchUserModel(module);
                                if (patched)
                                {
                                    module.Write(outputPath);
                                    return true;
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Skipping {entry.FullName}: {ex.Message}");
                                continue;
                            }
                        }
                    }
                }
                catch (InvalidDataException)
                {
                    Console.WriteLine("Input is not a ZIP/SFX archive.");
                }

                // Heuristic: scan the file for an embedded ZIP local file header (PK\x03\x04) and try from there (handles some SFX formats)
                try
                {
                    var all = File.ReadAllBytes(inputPath);
                    int sig = -1;
                    byte[] pattern = new byte[] { 0x50, 0x4B, 0x03, 0x04 };
                    for (int i = 0; i < all.Length - pattern.Length; i++)
                    {
                        bool match = true;
                        for (int j = 0; j < pattern.Length; j++) { if (all[i + j] != pattern[j]) { match = false; break; } }
                        if (match) { sig = i; break; }
                    }

                    if (sig >= 0)
                    {
                        Console.WriteLine($"Found embedded ZIP signature at offset {sig}. Trying to open archive from there...");
                        using (var ms = new MemoryStream(all, sig, all.Length - sig))
                        using (var za = new ZipArchive(ms, ZipArchiveMode.Read))
                        {
                            Console.WriteLine($"Embedded archive detected with {za.Entries.Count} entries.");
                            foreach (var entry in za.Entries)
                            {
                                if (string.IsNullOrEmpty(entry.Name)) continue;
                                if (!entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                                var tempFile = Path.Combine(tempDir, entry.Name);
                                Console.WriteLine($"Extracting embedded candidate: {entry.FullName}");
                                entry.ExtractToFile(tempFile);
                                try
                                {
                                    var module = ModuleDefMD.Load(tempFile);
                                    Console.WriteLine($"Loaded candidate assembly: {entry.FullName}");
                                    var patched = PatchUserModel(module);
                                    if (patched)
                                    {
                                        module.Write(outputPath);
                                        return true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Skipping embedded {entry.FullName}: {ex.Message}");
                                    continue;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Embedded ZIP scan failed: {ex.Message}");
                }

                // Try extracting with 7z if available (handles many installer types: NSIS, Inno, SFX)
                try
                {
                    var psi = new ProcessStartInfo("7z", $"x -y -o\"{tempDir}\" \"{inputPath}\"")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var proc = Process.Start(psi))
                    {
                        if (proc != null)
                        {
                            proc.WaitForExit(120000); // wait up to 2 minutes
                            Console.WriteLine($"7z exit code: {proc.ExitCode}");
                            if (proc.ExitCode == 0 || proc.ExitCode == 1)
                            {
                                // Scan extracted files for candidates
                                foreach (var file in Directory.EnumerateFiles(tempDir, "*.*", SearchOption.AllDirectories))
                                {
                                    try
                                    {
                                        // If the file looks like a managed assembly, try to load directly.
                                        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                                        {
                                            try
                                            {
                                                var module = ModuleDefMD.Load(file);
                                                Console.WriteLine($"Loaded candidate from 7z extraction: {file}");
                                                var patched = PatchUserModel(module);
                                                if (patched)
                                                {
                                                    module.Write(outputPath);
                                                    return true;
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                Console.WriteLine($"Skipping extracted {file}: {ex.Message}");
                                            }
                                        }

                                        // Also scan large extracted blobs for embedded PE images (MZ header)
                                        var fi = new FileInfo(file);
                                        if (fi.Length > 1024)
                                        {
                                            var bytes = File.ReadAllBytes(file);
                                            for (int i = 0; i < bytes.Length - 1; i++)
                                            {
                                                if (bytes[i] == 0x4D && bytes[i + 1] == 0x5A) // 'MZ'
                                                {
                                                    try
                                                    {
                                                        using (var ms = new MemoryStream(bytes, i, bytes.Length - i))
                                                        {
                                                            var module = ModuleDefMD.Load(ms);
                                                            Console.WriteLine($"Loaded embedded PE candidate from {file} at offset {i}");
                                                            var patched = PatchUserModel(module);
                                                            if (patched)
                                                            {
                                                                module.Write(outputPath);
                                                                return true;
                                                            }
                                                        }
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        // Not a valid managed PE at this offset; continue searching
                                                        // Avoid loud logs for each failure on big files
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"Unexpected error scanning {file}: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"7z extraction attempt failed: {ex.Message}");
                }

                // No managed assembly found in installer
                return false;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
