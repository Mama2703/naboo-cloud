using Naboo.Api.Domain;
using static Naboo.Api.Services.ServiceRendezVous;

namespace Naboo.Api.Pages;

/// <summary>Page ouverte depuis le lien « Examiner la demande » de l'email : valider ou refuser en un geste.</summary>
public static class PageExamen
{
    public static string Html(RendezVous? rdv, Reglages r)
    {
        string corps;
        if (rdv is null)
        {
            corps = "<h1>Lien invalide</h1><p>Cette demande est introuvable ou le lien est incorrect.</p>";
        }
        else
        {
            var lieu = rdv.Lieu == "domicile" ? $"À domicile (+{r.SupplementDomicile}) : {rdv.Adresse}" : $"Chez {r.NomActivite}";
            corps =
                $"<h1>{H(rdv.Prestation)} — {H(rdv.Nom)}</h1><div class=\"quand\">{H(Dates.Lisible(rdv.Debut))}</div>" +
                "<dl>" +
                $"<dt>Lieu</dt><dd>{H(lieu)}</dd>" +
                $"<dt>Email</dt><dd>{H(rdv.Email)}</dd>" +
                $"<dt>Téléphone</dt><dd><a href=\"tel:{H(rdv.Telephone)}\">{H(rdv.Telephone)}</a></dd>" +
                (rdv.Message.Length > 0 ? $"<dt>Message</dt><dd>{H(rdv.Message)}</dd>" : "") +
                $"<dt>Statut</dt><dd><span class=\"statut\">{H(Statuts.Libelle(rdv.Statut))}</span></dd>" +
                "</dl>" +
                (rdv.Statut == Statuts.Attente
                    ? $"<p class=\"rappel\">Avant de valider, vérifie dans Wero que l'acompte de {H(r.Acompte)} est arrivé, avec le message « {H(MessageWero(rdv))} ».</p>" +
                      "<div class=\"actions\"><button class=\"ok\" data-d=\"valider\">Valider</button><button class=\"non\" data-d=\"refuser\">Refuser</button></div>"
                    : "") +
                "<div class=\"msg\" id=\"msg\" role=\"status\"></div>" +
                $"<script>const ID={System.Text.Json.JsonSerializer.Serialize(rdv.Id)},TOKEN={System.Text.Json.JsonSerializer.Serialize(rdv.Token)};" +
                """
                document.querySelectorAll('[data-d]').forEach(b => b.onclick = async () => {
                  const d = b.dataset.d;
                  if (d === 'refuser' && !confirm('Refuser cette demande ?')) return;
                  document.querySelectorAll('button').forEach(x => x.disabled = true);
                  const m = document.getElementById('msg');
                  try {
                    const r = await fetch('decision', { method: 'POST', headers: { 'Content-Type': 'application/json' },
                      body: JSON.stringify({ id: ID, token: TOKEN, decision: d }) });
                    const j = await r.json();
                    if (!j.ok) throw new Error(j.erreur);
                    m.textContent = j.message; m.className = 'msg';
                    document.querySelector('.actions').remove();
                  } catch (e) {
                    m.textContent = 'Erreur : ' + e.message; m.className = 'msg err';
                    document.querySelectorAll('button').forEach(x => x.disabled = false);
                  }
                  m.style.display = 'block';
                });
                </script>
                """;
        }

        return """
            <!DOCTYPE html><html lang="fr"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1"><meta name="robots" content="noindex">
            <title>Demande de rendez-vous</title>
            <style>
              body{font-family:-apple-system,system-ui,"Segoe UI",sans-serif;background:#f4ece2;color:#3a2a1c;margin:0;padding:24px 16px}
              .carte{max-width:460px;margin:0 auto;background:#fffaf4;border-radius:16px;padding:24px;box-shadow:0 2px 12px rgba(0,0,0,.06)}
              h1{font-size:20px;margin:0 0 4px}.quand{color:#8a6440;font-weight:600;margin-bottom:16px}
              dl{margin:0 0 20px}dt{font-size:12px;text-transform:uppercase;letter-spacing:.04em;color:#85705c;margin-top:10px}
              dd{margin:2px 0 0;word-break:break-word}dd a{color:#8a6440}
              .actions{display:flex;gap:10px}button{flex:1;border:0;border-radius:10px;padding:14px;font-size:16px;font-weight:600;cursor:pointer}
              .ok{background:#8a6440;color:#fff}.non{background:#f8e3df;color:#9a2f22}button:disabled{opacity:.5;cursor:wait}
              .rappel{background:#f4ece2;border-radius:10px;padding:10px 12px;font-size:14px;margin:0 0 14px}
              .msg{margin-top:16px;padding:12px;border-radius:10px;background:#e1f0e5;display:none}.msg.err{background:#f8e3df;color:#9a2f22}
              .statut{display:inline-block;padding:4px 10px;border-radius:99px;background:#efe2d2;font-size:13px}
            </style></head><body><div class="carte">
            """ + corps + "</div></body></html>";
    }
}
