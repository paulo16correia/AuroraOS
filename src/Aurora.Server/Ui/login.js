// Signs a person in with the operator passphrase, then hands over to the panel.

'use strict';

const form = document.getElementById('signin');
const field = document.getElementById('passphrase');
const note = document.getElementById('signin-note');

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  note.textContent = '';

  const button = form.querySelector('button[type="submit"]');
  button.disabled = true;

  try {
    const response = await fetch('/ui/session/passphrase', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'same-origin',
      body: JSON.stringify({ passphrase: field.value }),
    });

    if (response.ok) {
      window.location.assign('/ui/');
      return;
    }

    const body = await response.json().catch(() => null);
    note.textContent = body?.error_description || `Not signed in (${response.status}).`;
  } catch (error) {
    note.textContent = `Aurora is not answering: ${error.message}`;
  } finally {
    // Never left in the page longer than the attempt needs it.
    field.value = '';
    button.disabled = false;
  }
});
