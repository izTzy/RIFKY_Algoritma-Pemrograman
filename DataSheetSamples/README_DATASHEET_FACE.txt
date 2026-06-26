CONTOH DATASHEET FACE v13

Folder ini berisi contoh struktur datasheet dan placeholder wajah sintetis sederhana.
Saya tidak memasukkan foto wajah orang nyata dari internet karena foto biometric/identitas wajah perlu izin pemilik dan rawan hak cipta/privasi.

Cara pakai pada aplikasi:
1. Jalankan app.
2. Register user dan ambil foto wajah dari kamera.
3. Aplikasi otomatis membuat:
   - AppData/face_datasheet.csv
   - AppData/FaceSamples/<username>_sample_001.jpg
   - AppData/FaceSamples/<username>_sample_001_embedding.json
4. Untuk melatih logika lebih kuat, update wajah beberapa kali dari angle/cahaya berbeda.

Kolom datasheet runtime:
username,role,sample_id,photo_path,template_path,quality_score,created_at,source

Catatan penting:
- Haar Cascade tetap hanya mendeteksi wajah.
- Datasheet berisi banyak sample/template untuk dibandingkan saat login.
- Login diterima hanya jika:
  a) LBPH memprediksi label user saat ini,
  b) skor embedding user saat ini lolos threshold,
  c) skor tersebut lebih jauh dari skor sample user lain minimal 8 poin.
