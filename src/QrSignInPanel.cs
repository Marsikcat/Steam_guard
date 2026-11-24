using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LocalSteamGuard
{
    internal sealed class QrSignInPanel : Panel
    {
        private readonly SteamApi steam;
        private readonly QrView qr;
        private readonly Label status;
        private CancellationTokenSource active;

        public event Action<SteamLogin> SignedIn;

        public QrSignInPanel(SteamApi steam)
        {
            this.steam = steam;
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
            var layout = Theme.Rows(43, 54, 0, 44, 41);
            layout.Padding = new Padding(22, 0, 0, 0);
            Controls.Add(layout);
            layout.Controls.Add(Theme.Label("Вход по QR-коду", Theme.Account, Theme.Text), 0, 0);
            var instruction = Theme.Label(
                "Открой Steam Guard в Steam Mobile, отсканируй QR-код и подтверди вход.",
                Theme.Body, Theme.Muted);
            instruction.AutoEllipsis = false;
            layout.Controls.Add(instruction, 0, 1);
            qr = new QrView { Dock = DockStyle.Fill, Margin = Padding.Empty };
            layout.Controls.Add(qr, 0, 2);
            status = Theme.Label("", Theme.Body, Theme.Muted);
            status.AutoEllipsis = false;
            layout.Controls.Add(status, 0, 3);

            var buttons = Theme.ButtonRow(false);
            var refresh = Theme.Button("Обновить QR-код", 154);
            buttons.Controls.Add(refresh);
            layout.Controls.Add(buttons, 0, 4);
            refresh.Click += async delegate { await LoadCode(); };
        }

        public void Stop(string message = "")
        {
            CancelAttempt();
            qr.ShowCode(null);
            status.Text = message;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                CancelAttempt();
            base.Dispose(disposing);
        }

        private void CancelAttempt()
        {
            var previous = active;
            active = null;
            if (previous != null)
                previous.Cancel();
        }

        public async Task LoadCode()
        {
            CancelAttempt();
            using (var attempt = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                active = attempt;
                var cancel = attempt.Token;
                qr.ShowCode(null);
                status.Text = "Запрашиваю QR-код у Steam…";
                try
                {
                    var session = await steam.BeginQrLogin(cancel);
                    cancel.ThrowIfCancellationRequested();
                    qr.ShowCode(session.QrUrl);
                    status.Text = "Ожидаю подтверждение в Steam Mobile…";
                    var updates = new Progress<string>(url =>
                    {
                        if (active == attempt && !cancel.IsCancellationRequested)
                            qr.ShowCode(url);
                    });
                    await steam.WaitForLogin(session, cancel, updates);
                    cancel.ThrowIfCancellationRequested();
                    qr.ShowCode(null);
                    status.Text = "Вход подтверждён.";
                    var completed = SignedIn;
                    if (completed != null)
                        completed(session);
                }
                catch (Exception error)
                {
                    if (active == attempt && !IsDisposed)
                    {
                        qr.ShowCode(null);
                        var steamError = error as SteamApiError;
                        status.Text = cancel.IsCancellationRequested || (steamError != null && steamError.Result == 9)
                            ? "QR-код больше не активен. Обнови его для новой попытки."
                            : SteamUi.Error(error);
                    }
                }
                finally
                {
                    if (active == attempt)
                        active = null;
                }
            }
        }
    }
}
