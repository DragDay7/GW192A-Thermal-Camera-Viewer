# Third-party notices

The viewer uses **DirectShowLib.Standard 2.1.0**, a .NET wrapper for Windows
DirectShow interfaces, distributed under the GNU Lesser General Public License
2.1. Framework-dependent single-file publishing bundles its managed assembly
inside the viewer executable. An unbundled build can be produced with
`-p:PublishSingleFile=false` to keep the library as a separate, replaceable DLL.

- Package: https://www.nuget.org/packages/DirectShowLib.Standard/2.1.0
- Source: https://github.com/luthfiampas/DirectShowLib
- License text: `DirectShowLib-LICENSE.txt` in the published application directory.

The optional xBRZ display scaler uses **XbrzSharp 1.1.0**, a .NET port of xBRZ 1.8
by Zenju, based on Stanio's Java port. Copyright (c) 2025 Ho Tzin Mein.

- Package: https://www.nuget.org/packages/XbrzSharp/1.1.0
- Source: https://github.com/tzinmein/xbrz-sharp
- Core library license: GNU General Public License v3.0 only.
- License text: `XbrzSharp-LICENSE.txt` in the published application directory.

Distributing a combined application containing this GPL library requires complying
with GPLv3, including the applicable corresponding-source requirements. This is
not a permissively licensed dependency.
Single-file packaging does not remove these license obligations. Distribution
must include the applicable notices, licenses, and corresponding-source/rebuilding
materials required by the dependencies' licenses, including the ability to rebuild
with a modified DirectShow library.
