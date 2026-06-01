# Stellarium Landscape Creation Endpoint

This document describes the backend endpoint used to generate a Stellarium Web landscape package from a 360 equirectangular panorama.

## Endpoint

- Method: `POST`
- Path: `/api/stellarium/landscape/create`
- Content type: `multipart/form-data`
- Response content type: `application/zip`

## Multipart Fields

Required fields:

- `image`: panorama file (PNG, JPEG, or WebP)
- `name`: display name (for example `My Backyard`)
- `folderName`: folder-safe landscape name (for example `my_backyard`)
- `northOffsetDeg`: numeric rotation in range `0..360`

Optional fields:

- `description`
- `latitude`
- `longitude`
- `altitude`
- `author`

## Validation

The endpoint returns HTTP 400 for invalid input, including:

- missing required fields
- unsupported image format
- panorama dimensions not approximately 2:1
- too-small source image
- oversized upload
- invalid numeric values

## Output ZIP Structure

The ZIP payload contains exactly this structure:

- `<folderName>/properties`
- `<folderName>/description.en.utf8`
- `<folderName>/Norder0/Allsky.webp`
- `<folderName>/Norder0/Dir0/Npix0.webp`
- `<folderName>/Norder0/Dir0/Npix1.webp`
- `<folderName>/Norder0/Dir0/Npix2.webp`
- `<folderName>/Norder0/Dir0/Npix3.webp`
- `<folderName>/Norder0/Dir0/Npix4.webp`
- `<folderName>/Norder0/Dir0/Npix5.webp`
- `<folderName>/Norder0/Dir0/Npix6.webp`
- `<folderName>/Norder0/Dir0/Npix7.webp`
- `<folderName>/Norder0/Dir0/Npix8.webp`
- `<folderName>/Norder0/Dir0/Npix9.webp`
- `<folderName>/Norder0/Dir0/Npix10.webp`
- `<folderName>/Norder0/Dir0/Npix11.webp`

Every `Npix*.webp` tile is generated as `512x512` and encoded in WebP.

## Notes

- The north-alignment sign is centralized in the `ApplyNorthOffset` method in the landscape service for quick inversion after visual checks.
- Tile generation uses HEALPix base pixels (`order=0`) and bilinear sampling from the equirectangular source panorama.
