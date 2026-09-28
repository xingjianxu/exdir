using System;
using System.Collections.Generic;
using System.IO;
using Exdir.Models;

namespace Exdir.Services;

/// <inheritdoc cref="IDriveService" />
public sealed class DriveService : IDriveService
{
    public IReadOnlyList<DriveModel> GetDrives()
    {
        var result = new List<DriveModel>();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                result.Add(Create(drive));
            }
        }
        catch (Exception)
        {
            // 枚举失败（很少见）时返回已收集到的部分
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.RootPath, b.RootPath));
        return result;
    }

    private static DriveModel Create(DriveInfo drive)
    {
        var kind = drive.DriveType switch
        {
            DriveType.Fixed => DriveKind.Fixed,
            DriveType.Removable => DriveKind.Removable,
            DriveType.Network => DriveKind.Network,
            DriveType.CDRom => DriveKind.Optical,
            DriveType.Ram => DriveKind.Ram,
            _ => DriveKind.Unknown,
        };

        var root = drive.Name;
        var display = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(display))
        {
            display = root;
        }

        string label = string.Empty;
        string unc = string.Empty;
        long total = 0;
        long free = 0;
        var ready = false;

        try
        {
            ready = drive.IsReady;
            if (ready)
            {
                label = drive.VolumeLabel;
                total = drive.TotalSize;
                free = drive.AvailableFreeSpace;
            }
        }
        catch (Exception)
        {
            ready = false;
        }

        if (kind == DriveKind.Network)
        {
            try
            {
                unc = drive.RootDirectory.FullName;
            }
            catch (Exception)
            {
                unc = root;
            }
        }

        return new DriveModel
        {
            RootPath = root,
            DisplayName = display,
            VolumeLabel = label,
            UncPath = unc,
            Kind = kind,
            IsReady = ready,
            TotalSize = total,
            FreeSpace = free,
        };
    }
}
