# Tests

Use the [testing guide](../docs/TESTING.md) to select managed suites, Python
tool checks, native Editor integration or an installed consumer workflow.

`Finare.Tests/` retains stable test class names and fixture paths. xUnit
`Category` traits provide behavior groups without moving fixtures or duplicating
test projects. List the current groups from the repository root:

```powershell
python -B tools/test_suites.py
```

Native UE Automation cases remain with their Runtime/Editor modules under
`HostProject/Plugins/Finare/Source/`. Python tooling cases remain under
`tools/tests/` and are all discovered by `tools/run-tool-tests.py`.

The [workflow coverage map](../docs/TESTING.md#select-by-user-workflow) connects
cross-layer changes to their relevant tests. A category pass retains its managed
scope and does not imply native, installed-project or package acceptance.
