/**
 * Delicate API Adapter — Date Picker init.
 *
 * Reads window.daaDatePickerConfig (set inline by wp_add_inline_script
 * in DAA_Date_Picker::enqueue_assets) and binds flatpickr to the
 * #daa_delivery_date input.
 *
 * No build tooling — plain ES5/ES2015 to keep the dependency surface
 * tiny and avoid babel/parcel for a single 60-line file.
 */
(function () {
    'use strict';

    function init() {
        var cfg = window.daaDatePickerConfig || null;
        if (!cfg) { return; }

        var input = document.getElementById(cfg.fieldId || 'daa_delivery_date');
        if (!input) { return; }
        if (typeof window.flatpickr !== 'function') {
            // flatpickr failed to load — leave the input as a plain text box
            // (still works, just no nice calendar). Don't throw.
            if (window.console && console.warn) {
                console.warn('[DAA] flatpickr not loaded; date input will be a plain text box.');
            }
            return;
        }

        // Translate our config keys into flatpickr's option shape.
        var fpOpts = {
            dateFormat: cfg.dateFormat || 'Y-m-d',
            altInput:   !!cfg.altInput,
            altFormat:  cfg.altFormat || 'D, j M Y',
            minDate:    cfg.minDate || null,
            maxDate:    cfg.maxDate || null,
            allowInput: false,
            disableMobile: true,    // Force flatpickr's own UI on mobile too
                                    // (default would use the native input picker
                                    // which ignores our disabled-weekday and
                                    // blackout config).
            disable: []
        };

        // Disabled weekdays: build a function that returns true for the
        // dates we want to grey out. flatpickr's "disable" accepts both
        // date strings and functions in the same array.
        var disabledWeekdays = (cfg.disabledWeekdays || []).slice();
        if (disabledWeekdays.length) {
            fpOpts.disable.push(function (date) {
                return disabledWeekdays.indexOf(date.getDay()) !== -1;
            });
        }

        // Blackout dates: pushed as plain strings — flatpickr matches
        // them against dateFormat directly.
        var blackouts = cfg.blackouts || [];
        for (var i = 0; i < blackouts.length; i++) {
            fpOpts.disable.push(blackouts[i]);
        }

        // Placeholder (set on the visible input — altInput when enabled,
        // else the original).
        if (cfg.placeholder) {
            // flatpickr clones the input into the altInput; we set the
            // placeholder AFTER flatpickr has bound so altInput exists.
            fpOpts.onReady = function (selectedDates, dateStr, instance) {
                if (instance.altInput && cfg.placeholder) {
                    instance.altInput.setAttribute('placeholder', cfg.placeholder);
                } else if (instance.input && cfg.placeholder) {
                    instance.input.setAttribute('placeholder', cfg.placeholder);
                }
            };
        }

        try {
            window.flatpickr(input, fpOpts);
        } catch (e) {
            if (window.console && console.error) {
                console.error('[DAA] flatpickr init failed:', e);
            }
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
