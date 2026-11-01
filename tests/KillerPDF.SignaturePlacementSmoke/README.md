# Signature placement acceptance

This isolated WPF host loads the actual application, creates a synthetic PDF and drawn signature,
and exercises the picker and page events. Settings stay in the supplied scratch data directory.
Use a fresh data directory for every run. No window opens unless a native mode is selected.

Build the application and this project into task scratch with `--artifacts-path`.
Copy the current application `App.xaml` beside the application DLL for isolated resource loading.
Run the built smoke executable with the application DLL path, scratch data directory,
`--startup-smoke`, and one case:

- `--click`: select a saved signature, then press the page. Visible ink must appear once.
- `--drop`: select a signature and deliver the routed drop. Visible ink must appear once.
- `--outside`: a drop outside the page must add nothing.
- `--cancel`: switching to Select before a drop must add nothing.
- `--stale`: changing the document before a drop must add nothing.
- `--native-drag`: real pointer press, movement and release from picker to page.
- `--native-click`: real pointer click on the picker, then click on the page.

Native cases require an exclusive SN01 or SN02 lease and an unlocked interactive session.
They open the real window, save a rendered capture, and fail unless ink appears exactly once
and the placement instruction disappears. Publish this host as self-contained when the guest
has no shared .NET desktop runtime. Native cases must never run on the main PC.

The routed cases cover drop validation without a native window. They do not establish native
mouse behavior. The native pair tests the complete picker-to-page interaction.
