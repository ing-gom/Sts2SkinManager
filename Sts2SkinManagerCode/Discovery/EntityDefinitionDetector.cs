using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Sts2SkinManager.Discovery;

// Signal A: "this DLL defines new game entities". A skin mod swaps visuals; it never extends
// MonsterModel, EncounterModel, EventModel, CardModel, PowerModel, RelicModel, or PotionModel.
// Detecting a single such subclass in a mod assembly is enough to classify it as a content mod
// and exclude it from skin-mod auto-suggestion / DLL block.
//
// We read the DLL via System.Reflection.Metadata (PE/TypeDef parsing only — no assembly load),
// because the migration pass needs to run during MainFile.Initialize, BEFORE STS2's ModManager
// has loaded the other mods' DLLs. Reflection-based GetTypes() can't see types whose assembly
// hasn't been loaded yet; metadata reading works on any PE file on disk.
//
// Canonical false positive we want to catch:
//   Act4FinalAscent (Nexus #37) defines Act4ArchitectBoss : MonsterModel + 4 more entity
//   subclasses. It also patches NRestSiteCharacter._Ready (a spine target) — enough to land
//   in the byte-frequency suggester pipeline, where the Architect's defect-shaped art tips
//   the dominance ratio toward "defect" and the mod gets DLL-blocked whenever the user picks
//   a non-default Defect skin.
public static class EntityDefinitionDetector
{
    // (Namespace, TypeName) pairs of STS2 entity base classes. All entity bases live directly
    // under MegaCrit.Sts2.Core.Models (verified by inspecting sts2.dll — the `using` directives
    // in mod sources like `using MegaCrit.Sts2.Core.Models.Monsters;` reference subnamespaces
    // that contain concrete monster instances, NOT the abstract base class itself).
    // Matched against TypeDef.BaseType resolved through TypeReference rows.
    private static readonly (string Namespace, string Name)[] ContentEntityBases =
    {
        ("MegaCrit.Sts2.Core.Models", "MonsterModel"),
        ("MegaCrit.Sts2.Core.Models", "EncounterModel"),
        ("MegaCrit.Sts2.Core.Models", "EventModel"),
        ("MegaCrit.Sts2.Core.Models", "CardModel"),
        ("MegaCrit.Sts2.Core.Models", "PowerModel"),
        ("MegaCrit.Sts2.Core.Models", "RelicModel"),
        ("MegaCrit.Sts2.Core.Models", "PotionModel"),
    };

    public record EntityDefinitionReport(
        string ModId,
        IReadOnlyList<string> DefinedEntities,
        string? FirstEntityBaseName
    );

    // Reads the DLL at dllPath via PEReader + MetadataReader. Returns null when the file isn't
    // a valid PE/.NET assembly or defines no content-entity subclasses.
    public static EntityDefinitionReport? InspectFile(string modId, string dllPath)
    {
        if (!File.Exists(dllPath)) return null;

        try
        {
            using var stream = File.OpenRead(dllPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;
            var reader = pe.GetMetadataReader();

            // Walk TypeDef rows; for each, resolve its BaseType. If BaseType resolves to one of
            // our entity bases, count it. TypeDef.BaseType can be a TypeDefHandle (base defined
            // in this assembly — uninteresting for our case), a TypeReferenceHandle (base in
            // another assembly — the common case for "subclasses MonsterModel"), or a
            // TypeSpecificationHandle (generic instantiation — ignored).
            var defined = new List<string>();
            string? firstBase = null;

            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var typeDef = reader.GetTypeDefinition(typeHandle);
                if (typeDef.BaseType.IsNil) continue;

                if (typeDef.BaseType.Kind != HandleKind.TypeReference) continue;

                var typeRef = reader.GetTypeReference((TypeReferenceHandle)typeDef.BaseType);
                var baseNs = reader.GetString(typeRef.Namespace);
                var baseName = reader.GetString(typeRef.Name);

                foreach (var (ns, name) in ContentEntityBases)
                {
                    if (string.Equals(baseNs, ns, StringComparison.Ordinal) &&
                        string.Equals(baseName, name, StringComparison.Ordinal))
                    {
                        var thisNs = reader.GetString(typeDef.Namespace);
                        var thisName = reader.GetString(typeDef.Name);
                        var fullName = string.IsNullOrEmpty(thisNs) ? thisName : $"{thisNs}.{thisName}";
                        defined.Add(fullName);
                        firstBase ??= name;
                        break;
                    }
                }
            }

            if (defined.Count == 0) return null;
            return new EntityDefinitionReport(modId, defined, firstBase);
        }
        catch
        {
            // Best-effort: any malformed PE / IO failure → treat as "no signal", don't crash boot.
            return null;
        }
    }
}
