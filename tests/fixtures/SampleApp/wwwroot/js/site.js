// Fixture script exercising every client-side extraction rule.

// Rendered by the _Card partial, not by Index itself. Proves that data-* keys
// reach a script through the composed DOM, not just the page's own markup.
var greeting = document.querySelector('.card').dataset.greeting;

// Read via the attribute-selector form.
var picked = document.querySelector('.card[data-state]');

// Nothing renders data-missing-key. This is the defect the unbound-key report
// exists to catch: a rename that updated one side of the contract only.
var orphan = document.querySelector('.card').dataset.missingKey;

// Client-owned state: written here, so its absence from server markup is
// expected and must not be reported.
document.querySelector('.card').dataset.clientOwned = '1';

fetch('/api/Greetings/Get');

// The id contract, happy half: asp-for="Name" on Index renders id="Name".
var nameInput = document.getElementById('Name');

// ...and through the composed DOM: the _Card partial renders #card-title.
var title = document.querySelector('#card-title');

// Nothing renders #cart-count. The unbound-selector defect: a rename that
// updated the markup but not the script.
var badge = document.getElementById('cart-count');

// Self-created, then selected. Not a contract with the server.
var overlay = document.createElement('div');
overlay.id = 'popup-host';
document.getElementById('popup-host');
