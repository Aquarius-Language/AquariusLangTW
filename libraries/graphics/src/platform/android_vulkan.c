/* Android emulator: complete timeline values in vkQueueSubmit.
 *
 * The emulator's GFXStream Vulkan driver (vulkan.ranchu.so, API 37 images) reads
 * VkTimelineSemaphoreSubmitInfo::pWaitSemaphoreValues[i] for every wait semaphore, binary ones included, and crashes
 * when wgpu submits a binary wait (its submission-ordering semaphore) with a timeline signal and therefore no wait
 * values. Vulkan ignores the values of binary semaphores, so each such submission is passed on with both value arrays
 * padded to their semaphore counts (existing values kept, zeros added) - the correction the earlier runtime patched into
 * wgpu-hal (docs/android-api37.md).
 *
 * wgpu-native is linked statically and loads Vulkan itself (dlopen + dlsym "vkGetInstanceProcAddr"). The library links
 * with --wrap=dlsym (CMakeLists.txt), so on emulators that lookup returns a vkGetInstanceProcAddr which hands out the
 * padding vkQueueSubmit; every other dlsym call is passed through unchanged. Real devices are never affected. */
#include <dlfcn.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>
#include <sys/system_properties.h>
#include <vulkan/vulkan.h>

void *__real_dlsym(void *handle, const char *symbol);

static PFN_vkGetInstanceProcAddr real_instance_proc;
static PFN_vkGetDeviceProcAddr real_device_proc;
static PFN_vkQueueSubmit real_submit;

static bool emulator(void) {
    static int known = -1;
    if (known < 0) {
        char hardware[PROP_VALUE_MAX] = {0};
        __system_property_get("ro.hardware", hardware);
        known = strcmp(hardware, "ranchu") == 0 || strcmp(hardware, "goldfish") == 0;
    }
    return known == 1;
}

/* Values for `count` semaphores: the given ones, then zeros. */
static uint64_t *padded(const uint64_t *values, uint32_t given, uint32_t count) {
    uint64_t *out = calloc(count ? count : 1, sizeof *out);
    if (out && values && given) memcpy(out, values, (size_t)(given < count ? given : count) * sizeof *out);
    return out;
}

static VKAPI_ATTR VkResult VKAPI_CALL padded_submit(VkQueue queue, uint32_t count, const VkSubmitInfo *submits, VkFence fence) {
    bool needed = false;
    for (uint32_t i = 0; i < count; i++) {
        const VkTimelineSemaphoreSubmitInfo *t = submits[i].pNext;
        if (t && t->sType == VK_STRUCTURE_TYPE_TIMELINE_SEMAPHORE_SUBMIT_INFO &&
            (t->waitSemaphoreValueCount < submits[i].waitSemaphoreCount || t->signalSemaphoreValueCount < submits[i].signalSemaphoreCount))
            needed = true;
    }
    if (!needed) return real_submit(queue, count, submits, fence);
    VkSubmitInfo *copies = malloc(count * sizeof *copies);
    VkTimelineSemaphoreSubmitInfo *timelines = calloc(count, sizeof *timelines);
    uint64_t **arrays = calloc((size_t)count * 2, sizeof *arrays);
    if (!copies || !timelines || !arrays) { free(copies); free(timelines); free(arrays); return VK_ERROR_OUT_OF_HOST_MEMORY; }
    for (uint32_t i = 0; i < count; i++) {
        copies[i] = submits[i];
        const VkTimelineSemaphoreSubmitInfo *t = submits[i].pNext;
        if (!t || t->sType != VK_STRUCTURE_TYPE_TIMELINE_SEMAPHORE_SUBMIT_INFO) continue;
        timelines[i] = *t;
        arrays[i * 2] = padded(t->pWaitSemaphoreValues, t->waitSemaphoreValueCount, submits[i].waitSemaphoreCount);
        arrays[i * 2 + 1] = padded(t->pSignalSemaphoreValues, t->signalSemaphoreValueCount, submits[i].signalSemaphoreCount);
        timelines[i].waitSemaphoreValueCount = submits[i].waitSemaphoreCount;
        timelines[i].pWaitSemaphoreValues = arrays[i * 2];
        timelines[i].signalSemaphoreValueCount = submits[i].signalSemaphoreCount;
        timelines[i].pSignalSemaphoreValues = arrays[i * 2 + 1];
        copies[i].pNext = &timelines[i];
    }
    VkResult result = real_submit(queue, count, copies, fence);
    for (uint32_t i = 0; i < count * 2; i++) free(arrays[i]);
    free(arrays); free(timelines); free(copies);
    return result;
}

static VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL device_proc(VkDevice device, const char *name) {
    PFN_vkVoidFunction f = real_device_proc(device, name);
    if (f && strcmp(name, "vkQueueSubmit") == 0) { real_submit = (PFN_vkQueueSubmit)f; return (PFN_vkVoidFunction)padded_submit; }
    return f;
}

static VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL instance_proc(VkInstance instance, const char *name) {
    PFN_vkVoidFunction f = real_instance_proc(instance, name);
    if (!f || !name) return f;
    if (strcmp(name, "vkGetInstanceProcAddr") == 0) return (PFN_vkVoidFunction)instance_proc;
    if (strcmp(name, "vkGetDeviceProcAddr") == 0) { real_device_proc = (PFN_vkGetDeviceProcAddr)f; return (PFN_vkVoidFunction)device_proc; }
    if (strcmp(name, "vkQueueSubmit") == 0) { real_submit = (PFN_vkQueueSubmit)f; return (PFN_vkVoidFunction)padded_submit; }
    return f;
}

void *__wrap_dlsym(void *handle, const char *symbol) {
    void *f = __real_dlsym(handle, symbol);
    if (f && symbol && strcmp(symbol, "vkGetInstanceProcAddr") == 0 && emulator()) {
        real_instance_proc = (PFN_vkGetInstanceProcAddr)f;
        return (void *)instance_proc;
    }
    return f;
}
