# Installed EPUB font integration test

Run this Windows test project in its own process. It exercises the real installed-font
catalog and EPUB resolver against an isolated font directory, without changing installed
fonts. It requires Windows Arial. Set `KILLERPDF_TEST_ROOT` to the task scratch folder
before running `dotnet test`. No application window opens.
