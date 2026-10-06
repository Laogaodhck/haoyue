<script setup lang="ts">
import { CircleAlert, X } from '@lucide/vue'
import { nextTick, ref, watch } from 'vue'
import { confirmationState, settleConfirmation } from '../confirmation'

const dialog = ref<HTMLElement | null>(null)
const cancelButton = ref<HTMLButtonElement | null>(null)

// Danger dialogs default focus to "取消" (the safe action): a reflex Enter can never
// trigger the destructive path, and the keyboard-only flow works without a mouse.
watch(() => confirmationState.open, (open) => {
  if (open) void nextTick(() => cancelButton.value?.focus())
})

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    event.stopPropagation()
    settleConfirmation(false)
    return
  }
  if (event.key !== 'Tab' || !dialog.value) return
  // Minimal focus trap: Tab / Shift+Tab cycle through the dialog's own buttons so
  // focus can never fall back to the page behind the modal.
  const focusable = Array.from(dialog.value.querySelectorAll<HTMLElement>('button'))
  if (focusable.length === 0) return
  const first = focusable[0]!
  const last = focusable[focusable.length - 1]!
  const active = document.activeElement as HTMLElement | null
  if (event.shiftKey && (active === first || !dialog.value.contains(active))) {
    event.preventDefault()
    last.focus()
  } else if (!event.shiftKey && (active === last || !dialog.value.contains(active))) {
    event.preventDefault()
    first.focus()
  }
}
</script>

<template>
  <Transition name="modal-fade">
    <div v-if="confirmationState.open" class="modal-backdrop confirmation-backdrop" @mousedown.self="settleConfirmation(false)">
      <section ref="dialog" class="confirmation-dialog" role="alertdialog" aria-modal="true"
        aria-labelledby="confirmation-title" @keydown="handleKeydown">
        <header>
          <span class="confirmation-icon" :class="{ danger: confirmationState.danger }"><CircleAlert :size="21" /></span>
          <div>
            <h2 id="confirmation-title">{{ confirmationState.title }}</h2>
            <p>{{ confirmationState.message }}</p>
          </div>
          <button class="icon-button" title="关闭" @click="settleConfirmation(false)"><X :size="18" /></button>
        </header>
        <footer>
          <button ref="cancelButton" class="secondary-button" @click="settleConfirmation(false)">取消</button>
          <button
            class="secondary-button primary-action"
            :class="{ 'confirm-danger': confirmationState.danger }"
            @click="settleConfirmation(true)"
          >{{ confirmationState.confirmLabel }}</button>
        </footer>
      </section>
    </div>
  </Transition>
</template>
