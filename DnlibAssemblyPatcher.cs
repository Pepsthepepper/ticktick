using System;
using System.IO;
using System.Linq;
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

                // Load the assembly
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
    }
}
