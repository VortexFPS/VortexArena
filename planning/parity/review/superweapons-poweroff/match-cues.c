/* Match the first 300 ms of a reference Ogg against a 48 kHz stereo s16le monitor recording. */
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/stat.h>

/* Minimal libsndfile ABI for machines with the runtime library but no development headers. */
typedef int64_t sf_count_t;
typedef struct {
    sf_count_t frames;
    int samplerate;
    int channels;
    int format;
    int sections;
    int seekable;
} SF_INFO;

extern void *sf_open(const char *, int, SF_INFO *);
extern sf_count_t sf_readf_float(void *, float *, sf_count_t);
extern int sf_close(void *);

int main(int argc, char **argv)
{
    if (argc != 3) {
        fprintf(stderr, "usage: %s REFERENCE.ogg MONITOR.raw\n", argv[0]);
        return 2;
    }

    enum { Rate = 48000, Stride = 32, TemplateSamples = 450, ScanStep = 4 };
    SF_INFO info = {0};
    void *ogg = sf_open(argv[1], 0x10, &info);
    if (!ogg)
        return 3;

    float *reference = calloc(info.frames * info.channels, sizeof(float));
    if (!reference || sf_readf_float(ogg, reference, info.frames) != info.frames)
        return 4;
    sf_close(ogg);

    float template[TemplateSamples];
    float template_energy = 0;
    for (int j = 0; j < TemplateSamples; ++j) {
        double source_position = (double)(j * Stride) * info.samplerate / Rate;
        int frame = (int)source_position;
        if (frame + 1 >= info.frames)
            return 5;
        double blend = source_position - frame;
        template[j] = reference[frame * info.channels] * (1 - blend)
            + reference[(frame + 1) * info.channels] * blend;
        template_energy += template[j] * template[j];
    }
    free(reference);

    struct stat monitor;
    if (stat(argv[2], &monitor))
        return 6;
    const size_t bytes_per_frame = 2 * sizeof(int16_t);
    int frames = monitor.st_size / bytes_per_frame;
    int16_t *audio = malloc(monitor.st_size);
    FILE *pcm = fopen(argv[2], "rb");
    if (!audio || !pcm || fread(audio, bytes_per_frame, frames, pcm) != (size_t)frames)
        return 7;
    fclose(pcm);

    printf("duration %.3f s\n", (double)frames / Rate);
    float peak = 0;
    float peak_gain = 0;
    int peak_frame = 0;
    int scan_end = frames - TemplateSamples * Stride;
    for (int i = 0; i < scan_end; i += ScanStep) {
        float cross = 0;
        float energy = 0;
        for (int j = 0; j < TemplateSamples; ++j) {
            int sample = 2 * (i + j * Stride);
            int mono = (audio[sample] + audio[sample + 1]) / 2;
            cross += mono * template[j];
            energy += (float)mono * mono;
        }

        float correlation = energy > 0 ? cross / sqrtf(energy * template_energy) : 0;
        if (correlation > 0.50f && correlation > peak) {
            peak = correlation;
            peak_frame = i;
            peak_gain = cross / template_energy / 32768.0f;
        }
        if (peak > 0 && (i - peak_frame > Rate * 0.24f || i + ScanStep >= scan_end)) {
            printf("%.3f s  corr %.3f gain %.3f\n", (double)peak_frame / Rate, peak, peak_gain);
            peak = 0;
        }
    }
    free(audio);
    return 0;
}
