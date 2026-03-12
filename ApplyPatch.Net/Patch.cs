using System.Text.RegularExpressions;

namespace ApplyPatch;

public enum PatchOperationType
{
	delete_file,
	create_file,
	update_file
}

public record ApplyPatchOperation(PatchOperationType type, string path, string diff);

public class Patch
{
	public static IEnumerable<ApplyPatchOperation> ParseV4APatchOperations(string patch)
	{
		var lines = Regex.Split(patch, "\r?\n")
			.Select(l => l.TrimEnd('\r'))
			.Where(l => !l.Trim().Equals("*** Begin Patch", StringComparison.OrdinalIgnoreCase))
			.ToList();

		PatchOperationType? currentType = null;
		string? currentPath = null;
		var buffer = new List<string>();

		IEnumerable<ApplyPatchOperation> Flush()
		{
			if (currentType != null && currentPath != null)
			{
				yield return new ApplyPatchOperation(
					currentType.Value,
					currentPath,
					string.Join("\n", buffer)
				);
			}

			buffer.Clear();
			currentType = null;
			currentPath = null;
		}

		foreach (var line in lines)
		{
			if (line.StartsWith("*** ", StringComparison.Ordinal))
			{
				if (line.StartsWith("*** Update File:", StringComparison.OrdinalIgnoreCase))
				{
					foreach (var op in Flush())
						yield return op;
					currentType = PatchOperationType.update_file;
					currentPath = line.Substring("*** Update File:".Length).Trim();
					continue;
				}

				if (line.StartsWith("*** Add File:", StringComparison.OrdinalIgnoreCase))
				{
					foreach (var op in Flush())
						yield return op;
					currentType = PatchOperationType.create_file;
					currentPath = line.Substring("*** Add File:".Length).Trim();
					continue;
				}

				if (line.StartsWith("*** Set File:", StringComparison.OrdinalIgnoreCase))
				{
					foreach (var op in Flush())
						yield return op;
					currentType = PatchOperationType.create_file;
					currentPath = line.Substring("*** Set File:".Length).Trim();
					continue;
				}

				if (line.StartsWith("*** Delete File:", StringComparison.OrdinalIgnoreCase))
				{
					foreach (var op in Flush())
						yield return op;
					currentType = PatchOperationType.delete_file;
					currentPath = line.Substring("*** Delete File:".Length).Trim();
					continue;
				}

				if (line.StartsWith("*** End Patch", StringComparison.OrdinalIgnoreCase))
				{
					break;
				}
			}

			if (currentType != null)
				buffer.Add(line);
		}

		foreach (var op in Flush())
			yield return op;
	}

	public static string ApplyDiff(string input, ApplyPatchOperation op)
	{
		DiffApplier.ApplyDiffMode mode = op.type == PatchOperationType.create_file 
			? DiffApplier.ApplyDiffMode.Create 
			: DiffApplier.ApplyDiffMode.Default;

		return DiffApplier.ApplyDiff(input, op.diff, mode);
	}
}
